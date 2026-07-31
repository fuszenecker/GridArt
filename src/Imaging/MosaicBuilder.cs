using System.Diagnostics;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace gridart.Imaging;

/// <summary>Quality figures for a finished mosaic, reported so runs can be compared objectively.</summary>
/// <param name="MeanDeltaE">
/// Mean CIE76 ΔE between each mosaic cell and the base-image region it stands in for. This is the
/// "zoomed out" likeness: under ~2.3 the average cell is within one just-noticeable difference.
/// </param>
/// <param name="P95DeltaE">95th-percentile cell ΔE — how bad the worst cells are.</param>
/// <param name="WorstDeltaE">Largest single-cell ΔE.</param>
/// <param name="DistinctTiles">How many distinct source images were used.</param>
public readonly record struct MosaicQuality(
    double MeanDeltaE,
    double P95DeltaE,
    double WorstDeltaE,
    int DistinctTiles);

/// <summary>Result of a mosaic run.</summary>
/// <param name="Image">The rendered mosaic. The caller owns and must dispose it.</param>
public sealed record MosaicResult(
    Image<Rgba32> Image,
    int Columns,
    int Rows,
    MosaicQuality Quality) : IDisposable
{
    public void Dispose() => Image.Dispose();
}

/// <summary>
/// Builds a photomosaic: the base image is divided into a grid of cells, each cell is matched to the
/// perceptually closest source image, and the chosen images are drawn at tile resolution. The output
/// keeps the base image's aspect ratio, so zoomed out it reads as the base image while zoomed in it
/// resolves into the individual pictures.
/// </summary>
public sealed class MosaicBuilder(ILogger<MosaicBuilder> logger)
{
    /// <summary>
    /// Additive score penalty, in squared-ΔE units, applied to a tile that already appears within
    /// <see cref="MosaicOptions.RepeatAvoidanceRadius"/> cells. Sized so it outweighs ordinary colour
    /// differences without ever making placement impossible.
    /// </summary>
    private const float RepeatPenalty = 900f;

    public async Task<MosaicResult> BuildAsync(
        MosaicOptions options,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        using var baseImage = await Image.LoadAsync<Rgba32>(options.BaseImage, cancellationToken);
        logger.LogInformation(
            "Base image {Path} is {Width}x{Height}.",
            options.BaseImage, baseImage.Width, baseImage.Height);

        var (columns, rows) = ResolveGrid(baseImage.Size, options.TilesAcross);
        var cellSize = new Size(options.TileSize, options.TileSize);
        var outputSize = new Size(columns * options.TileSize, rows * options.TileSize);

        logger.LogInformation(
            "Grid {Columns}x{Rows} = {Cells} cells; output {OutWidth}x{OutHeight}.",
            columns, rows, columns * rows, outputSize.Width, outputSize.Height);

        if (options.ClearCache)
        {
            var removed = TileCache.Clear(options.CacheDirectory, logger);
            logger.LogInformation("Cleared {Count} cache file(s).", removed);
        }

        // Keyed on the tiles folder and TileSize only — the two things the cached pixels depend on.
        var cache = options.NoCache
            ? null
            : TileCache.Open(options.CacheDirectory, options.TilesFolder, options.TileSize, logger);

        using var library = await TileLibrary.LoadAsync(
            options.TilesFolder, cellSize, options.SignatureGrid, options.Recursive, logger,
            cancellationToken, cache);

        if (options.MaxTileReuse > 0)
        {
            var capacity = (long)library.Tiles.Count * options.MaxTileReuse;
            if (capacity < (long)columns * rows)
            {
                throw new InvalidOperationException(
                    $"{library.Tiles.Count} tile(s) with MaxTileReuse={options.MaxTileReuse} can fill only " +
                    $"{capacity} of {(long)columns * rows} cells. Add more images, raise MaxTileReuse " +
                    "(0 = unlimited), or lower TilesAcross.");
            }
        }

        // The base image is resampled to the exact output size once; the per-pixel colour-adjust step
        // then has a matching target pixel for every mosaic pixel, so tinting can follow gradients
        // inside a cell instead of pushing the whole cell toward one flat colour.
        using var baseAtOutputScale = baseImage.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = outputSize,
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3,
        }));

        var cellSignatures = ComputeCellSignatures(baseImage, columns, rows, options.SignatureGrid);
        var assignment = Assign(cellSignatures, library, columns, rows, options);

        cancellationToken.ThrowIfCancellationRequested();

        var mosaic = Render(assignment, library, columns, rows, options.TileSize);
        try
        {
            if (options.ColorAdjustStrength > 0d)
            {
                ApplyColorAdjust(mosaic, baseAtOutputScale, (float)options.ColorAdjustStrength);
            }

            var quality = Measure(mosaic, baseImage, columns, rows, options.SignatureGrid, assignment);

            logger.LogInformation(
                "Mosaic built in {Elapsed:0.0}s — mean ΔE {Mean:0.00}, p95 ΔE {P95:0.00}, worst ΔE {Worst:0.00}, {Distinct} distinct tile(s).",
                stopwatch.Elapsed.TotalSeconds, quality.MeanDeltaE, quality.P95DeltaE, quality.WorstDeltaE, quality.DistinctTiles);

            return new MosaicResult(mosaic, columns, rows, quality);
        }
        catch
        {
            mosaic.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Lays <paramref name="tilesAcross"/> tiles along the longer axis and scales the other axis to
    /// preserve the aspect ratio, so the mosaic is never stretched relative to the base image.
    /// </summary>
    internal static (int Columns, int Rows) ResolveGrid(Size baseSize, int tilesAcross)
    {
        if (baseSize.Width >= baseSize.Height)
        {
            var columns = tilesAcross;
            var rows = Math.Max(1, (int)Math.Round(tilesAcross * (double)baseSize.Height / baseSize.Width));
            return (columns, rows);
        }

        var r = tilesAcross;
        var c = Math.Max(1, (int)Math.Round(tilesAcross * (double)baseSize.Width / baseSize.Height));
        return (c, r);
    }

    private static ColorSignature[] ComputeCellSignatures(
        Image<Rgba32> baseImage, int columns, int rows, int signatureGrid)
    {
        var signatures = new ColorSignature[columns * rows];

        for (var row = 0; row < rows; row++)
        {
            // Boundaries come from scaled integer division so cells tile the base image exactly and
            // no row or column of source pixels is dropped or double-counted.
            var top = (int)((long)row * baseImage.Height / rows);
            var bottom = (int)((long)(row + 1) * baseImage.Height / rows);
            if (bottom <= top)
            {
                bottom = Math.Min(baseImage.Height, top + 1);
            }

            for (var col = 0; col < columns; col++)
            {
                var left = (int)((long)col * baseImage.Width / columns);
                var right = (int)((long)(col + 1) * baseImage.Width / columns);
                if (right <= left)
                {
                    right = Math.Min(baseImage.Width, left + 1);
                }

                signatures[row * columns + col] = ColorSignature.Compute(
                    baseImage,
                    new Rectangle(left, top, right - left, bottom - top),
                    signatureGrid);
            }
        }

        return signatures;
    }

    /// <summary>
    /// Greedy nearest-signature assignment with reuse caps and local repeat avoidance. Cells are
    /// visited in a fixed order, so a run is reproducible for the same inputs and options.
    /// </summary>
    private int[] Assign(
        ColorSignature[] cellSignatures,
        TileLibrary library,
        int columns,
        int rows,
        MosaicOptions options)
    {
        var tiles = library.Tiles;
        var assignment = new int[columns * rows];
        Array.Fill(assignment, -1);

        var radius = options.RepeatAvoidanceRadius;
        var reuseCap = options.MaxTileReuse == 0 ? int.MaxValue : options.MaxTileReuse;

        for (var row = 0; row < rows; row++)
        {
            for (var col = 0; col < columns; col++)
            {
                var cellIndex = row * columns + col;
                var cell = cellSignatures[cellIndex];

                var bestScore = float.PositiveInfinity;
                var bestTile = -1;

                for (var t = 0; t < tiles.Count; t++)
                {
                    var tile = tiles[t];
                    if (tile.UseCount >= reuseCap)
                    {
                        continue;
                    }

                    var score = cell.DistanceTo(tile.Signature);
                    if (score >= bestScore)
                    {
                        // Even the unpenalised score already loses; skip the neighbour scan.
                        continue;
                    }

                    if (radius > 0 && IsUsedNearby(assignment, columns, rows, col, row, radius, t))
                    {
                        score += RepeatPenalty;
                        if (score >= bestScore)
                        {
                            continue;
                        }
                    }

                    bestScore = score;
                    bestTile = t;
                }

                if (bestTile < 0)
                {
                    throw new InvalidOperationException(
                        $"Ran out of usable tiles at cell ({col}, {row}) because every image hit the " +
                        $"MaxTileReuse limit of {options.MaxTileReuse}.");
                }

                assignment[cellIndex] = bestTile;
                tiles[bestTile].UseCount++;
            }
        }

        return assignment;
    }

    private static bool IsUsedNearby(
        int[] assignment, int columns, int rows, int col, int row, int radius, int tileIndex)
    {
        var fromRow = Math.Max(0, row - radius);
        var toCol = Math.Min(columns - 1, col + radius);

        for (var r = fromRow; r <= row; r++)
        {
            var fromCol = Math.Max(0, col - radius);
            // The current row is only filled up to the cell before this one.
            var lastCol = r == row ? col - 1 : toCol;

            for (var c = fromCol; c <= lastCol; c++)
            {
                if (assignment[r * columns + c] == tileIndex)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static Image<Rgba32> Render(
        int[] assignment, TileLibrary library, int columns, int rows, int tileSize)
    {
        var mosaic = new Image<Rgba32>(columns * tileSize, rows * tileSize);
        try
        {
            mosaic.Mutate(ctx =>
            {
                for (var row = 0; row < rows; row++)
                {
                    for (var col = 0; col < columns; col++)
                    {
                        var tile = library.Tiles[assignment[row * columns + col]];
                        ctx.DrawImage(tile.Pixels, new Point(col * tileSize, row * tileSize), 1f);
                    }
                }
            });

            return mosaic;
        }
        catch
        {
            mosaic.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Blends every mosaic pixel toward the corresponding base-image pixel in linear light.
    /// Because the target varies per pixel, tile texture survives even at high strength — at
    /// strength 1 the result is exactly the base image, at 0 the tiles are untouched.
    /// </summary>
    private static void ApplyColorAdjust(Image<Rgba32> mosaic, Image<Rgba32> baseAtOutputScale, float strength)
    {
        Debug.Assert(mosaic.Size == baseAtOutputScale.Size, "Colour-adjust target must match the mosaic size.");

        var keep = 1f - strength;

        mosaic.ProcessPixelRows(baseAtOutputScale, (mosaicAccessor, targetAccessor) =>
        {
            for (var y = 0; y < mosaicAccessor.Height; y++)
            {
                var mosaicRow = mosaicAccessor.GetRowSpan(y);
                var targetRow = targetAccessor.GetRowSpan(y);

                for (var x = 0; x < mosaicRow.Length; x++)
                {
                    var m = mosaicRow[x];
                    var t = targetRow[x];

                    mosaicRow[x] = new Rgba32(
                        ColorMath.LinearToSrgb(ColorMath.SrgbToLinear(m.R) * keep + ColorMath.SrgbToLinear(t.R) * strength),
                        ColorMath.LinearToSrgb(ColorMath.SrgbToLinear(m.G) * keep + ColorMath.SrgbToLinear(t.G) * strength),
                        ColorMath.LinearToSrgb(ColorMath.SrgbToLinear(m.B) * keep + ColorMath.SrgbToLinear(t.B) * strength),
                        byte.MaxValue);
                }
            }
        });
    }

    /// <summary>
    /// Scores the finished mosaic against the base image cell by cell. Comparing per cell rather
    /// than per pixel is the point: it measures the likeness that survives zooming out, which is
    /// exactly what a photomosaic is supposed to preserve.
    /// </summary>
    private static MosaicQuality Measure(
        Image<Rgba32> mosaic,
        Image<Rgba32> baseImage,
        int columns,
        int rows,
        int signatureGrid,
        int[] assignment)
    {
        var baseCells = ComputeCellSignatures(baseImage, columns, rows, signatureGrid);
        var mosaicCells = ComputeCellSignatures(mosaic, columns, rows, signatureGrid);

        var deltas = new double[baseCells.Length];
        var total = 0d;

        for (var i = 0; i < baseCells.Length; i++)
        {
            // DistanceTo is a mean of squared ΔE values, so the square root is a ΔE again.
            var delta = Math.Sqrt(baseCells[i].DistanceTo(mosaicCells[i]));
            deltas[i] = delta;
            total += delta;
        }

        Array.Sort(deltas);
        var p95Index = Math.Clamp((int)Math.Ceiling(deltas.Length * 0.95) - 1, 0, deltas.Length - 1);

        return new MosaicQuality(
            MeanDeltaE: total / deltas.Length,
            P95DeltaE: deltas[p95Index],
            WorstDeltaE: deltas[^1],
            DistinctTiles: assignment.Distinct().Count());
    }
}
