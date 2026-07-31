using System.Diagnostics;
using gridart.Progress;
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
        CancellationToken cancellationToken,
        IProgressReporter? progress = null)
    {
        progress ??= NullProgressReporter.Instance;
        var stopwatch = Stopwatch.StartNew();

        Image<Rgba32> baseImage;
        using (progress.Begin("Reading base image"))
        {
            baseImage = await Image.LoadAsync<Rgba32>(options.BaseImage, cancellationToken);
        }

        using var _ = baseImage;

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
        using var cache = options.NoCache
            ? null
            : TileCache.Open(options.CacheDirectory, options.TilesFolder, options.TileSize, logger);

        // The base image is resampled to the exact output size once; the per-pixel colour-adjust step
        // then has a matching target pixel for every mosaic pixel, so tinting can follow gradients
        // inside a cell instead of pushing the whole cell toward one flat colour.
        Image<Rgba32> baseAtOutputScale;
        using (progress.Begin("Rescaling base image"))
        {
            baseAtOutputScale = baseImage.Clone(ctx => ctx.Resize(new ResizeOptions
            {
                Size = outputSize,
                Mode = ResizeMode.Stretch,
                Sampler = KnownResamplers.Lanczos3,
            }));
        }

        using var __ = baseAtOutputScale;

        var cellCount = (long)columns * rows;

        // Analysing the base image comes *before* loading tiles even though matching needs both. It
        // depends only on the base image, and having it ready up front is what lets intermediate
        // previews be rendered while tens of thousands of tiles are still decoding.
        ColorSignature[] cellSignatures;
        using (var phase = progress.Begin("Analysing base image", cellCount, "cells"))
        {
            cellSignatures = ComputeCellSignatures(
                baseImage, columns, rows, options.SignatureGrid, phase, cancellationToken);
        }

        var stages = StageWriter.Create(
            options, cellSignatures, columns, rows, baseAtOutputScale, logger, progress);

        using var library = await TileLibrary.LoadAsync(
            options.TilesFolder, cellSize, options.SignatureGrid, options.Recursive, logger,
            cancellationToken, cache, progress, stages);

        if (options.MaxTileReuse > 0)
        {
            var capacity = (long)library.Tiles.Count * options.MaxTileReuse;
            if (capacity < cellCount)
            {
                throw new InvalidOperationException(
                    $"{library.Tiles.Count} tile(s) with MaxTileReuse={options.MaxTileReuse} can fill only " +
                    $"{capacity} of {cellCount} cells. Add more images, raise MaxTileReuse " +
                    "(0 = unlimited), or lower TilesAcross.");
            }
        }

        int[] assignment;
        using (var phase = progress.Begin("Matching tiles", cellCount, "cells"))
        {
            assignment = Assign(
                cellSignatures, library.Tiles, columns, rows,
                options.RepeatAvoidanceRadius, options.MaxTileReuse, phase, cancellationToken);
        }

        cancellationToken.ThrowIfCancellationRequested();

        Image<Rgba32> mosaic;
        using (var phase = progress.Begin("Rendering mosaic", cellCount, "tiles"))
        {
            mosaic = Render(assignment, library.Tiles, columns, rows, options.TileSize, phase);
        }

        try
        {
            if (options.ColorAdjustStrength > 0d)
            {
                using var phase = progress.Begin("Colour matching", mosaic.Height, "rows");
                ApplyColorAdjust(mosaic, baseAtOutputScale, (float)options.ColorAdjustStrength, phase);
            }

            MosaicQuality quality;
            using (var phase = progress.Begin("Scoring likeness", cellCount * 2, "cells"))
            {
                quality = Measure(
                    mosaic, baseImage, columns, rows, options.SignatureGrid, assignment, phase, cancellationToken);
            }

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
        Image<Rgba32> baseImage,
        int columns,
        int rows,
        int signatureGrid,
        IProgressPhase? phase = null,
        CancellationToken cancellationToken = default)
    {
        var signatures = new ColorSignature[columns * rows];

        for (var row = 0; row < rows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

            // Reported per row rather than per cell: a row is a coarse enough unit to keep the
            // counter cheap, and fine enough to animate on any realistic grid.
            phase?.Advance(columns);
        }

        return signatures;
    }

    /// <summary>
    /// Greedy nearest-signature assignment with reuse caps and local repeat avoidance. Cells are
    /// visited in a fixed order, so a run is reproducible for the same inputs and options.
    /// </summary>
    /// <remarks>
    /// Use counts are local to the call rather than stored on <see cref="Tile"/>. That keeps the method
    /// free of side effects on the library, which is what lets an intermediate stage render from the
    /// same tiles without perturbing the final mosaic.
    /// </remarks>
    private static int[] Assign(
        ColorSignature[] cellSignatures,
        IReadOnlyList<Tile> tiles,
        int columns,
        int rows,
        int repeatAvoidanceRadius,
        int maxTileReuse,
        IProgressPhase? phase = null,
        CancellationToken cancellationToken = default)
    {
        var assignment = new int[columns * rows];
        Array.Fill(assignment, -1);

        var useCounts = new int[tiles.Count];
        var radius = repeatAvoidanceRadius;
        var reuseCap = maxTileReuse == 0 ? int.MaxValue : maxTileReuse;

        for (var row = 0; row < rows; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var col = 0; col < columns; col++)
            {
                var cellIndex = row * columns + col;
                var cell = cellSignatures[cellIndex];

                var bestScore = float.PositiveInfinity;
                var bestTile = -1;

                for (var t = 0; t < tiles.Count; t++)
                {
                    if (useCounts[t] >= reuseCap)
                    {
                        continue;
                    }

                    var score = cell.DistanceTo(tiles[t].Signature);
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
                        $"MaxTileReuse limit of {maxTileReuse}.");
                }

                assignment[cellIndex] = bestTile;
                useCounts[bestTile]++;
            }

            phase?.Advance(columns);
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
        int[] assignment,
        IReadOnlyList<Tile> tiles,
        int columns,
        int rows,
        int tileSize,
        IProgressPhase? phase = null)
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
                        var tile = tiles[assignment[row * columns + col]];
                        ctx.DrawImage(tile.Pixels, new Point(col * tileSize, row * tileSize), 1f);
                    }

                    phase?.Advance(columns);
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
    private static void ApplyColorAdjust(
        Image<Rgba32> mosaic,
        Image<Rgba32> baseAtOutputScale,
        float strength,
        IProgressPhase? phase = null)
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

                phase?.Advance();
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
        int[] assignment,
        IProgressPhase? phase = null,
        CancellationToken cancellationToken = default)
    {
        // Two passes over the grid, which is why the phase total is 2x the cell count.
        var baseCells = ComputeCellSignatures(baseImage, columns, rows, signatureGrid, phase, cancellationToken);
        var mosaicCells = ComputeCellSignatures(mosaic, columns, rows, signatureGrid, phase, cancellationToken);

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

    /// <summary>
    /// Renders a mosaic from the tiles loaded so far and writes it beside the real output, so a run
    /// over tens of thousands of images shows its development instead of going quiet for many minutes.
    /// </summary>
    /// <remarks>
    /// A stage is a preview and is deliberately not the same computation as the final mosaic:
    /// <list type="bullet">
    /// <item>reuse caps are ignored, so a stage built from the first few hundred tiles cannot fail
    /// where the finished mosaic would succeed;</item>
    /// <item>likeness is not scored — that is two extra passes over the grid for a throwaway image;</item>
    /// <item>tiles arrive in load order rather than sorted order, so stages are not reproducible.
    /// The final mosaic is, and nothing here touches it: <see cref="Assign"/> keeps its use counts
    /// local, and the tile pixels are only read.</item>
    /// </list>
    /// </remarks>
    private sealed class StageWriter : ITileStageWriter
    {
        private readonly StageSchedule schedule;
        private readonly ColorSignature[] cellSignatures;
        private readonly int columns;
        private readonly int rows;
        private readonly int tileSize;
        private readonly float colorAdjustStrength;
        private readonly int repeatAvoidanceRadius;
        private readonly Image<Rgba32> baseAtOutputScale;
        private readonly string pathWithoutExtension;
        private readonly string extension;
        private readonly ILogger logger;
        private readonly IProgressReporter progress;

        private StageWriter(
            MosaicOptions options,
            ColorSignature[] cellSignatures,
            int columns,
            int rows,
            Image<Rgba32> baseAtOutputScale,
            ILogger logger,
            IProgressReporter progress)
        {
            schedule = new StageSchedule(TimeSpan.FromSeconds(options.StageIntervalSeconds));
            this.cellSignatures = cellSignatures;
            this.columns = columns;
            this.rows = rows;
            tileSize = options.TileSize;
            colorAdjustStrength = (float)options.ColorAdjustStrength;
            repeatAvoidanceRadius = options.RepeatAvoidanceRadius;
            this.baseAtOutputScale = baseAtOutputScale;
            this.logger = logger;
            this.progress = progress;

            var output = options.ResolveOutputPath();
            extension = Path.GetExtension(output) is { Length: > 0 } ext ? ext : ".png";
            pathWithoutExtension = Path.Combine(
                Path.GetDirectoryName(output) ?? ".",
                Path.GetFileNameWithoutExtension(output));
        }

        /// <summary>Returns null when stages are switched off, so the load loop skips the check entirely.</summary>
        public static ITileStageWriter? Create(
            MosaicOptions options,
            ColorSignature[] cellSignatures,
            int columns,
            int rows,
            Image<Rgba32> baseAtOutputScale,
            ILogger logger,
            IProgressReporter progress)
        {
            if (options.StageIntervalSeconds <= 0)
            {
                return null;
            }

            return new StageWriter(
                options, cellSignatures, columns, rows, baseAtOutputScale, logger, progress);
        }

        public async Task<bool> TryWriteAsync(
            Func<IReadOnlyList<Tile>> snapshot, CancellationToken cancellationToken)
        {
            // Claiming is what makes this exclusive: the check runs on whichever loader thread gets
            // here first, and only one may render at a time.
            if (!schedule.TryClaim(out var index))
            {
                return false;
            }

            var clock = Stopwatch.StartNew();
            var produced = false;

            try
            {
                var tiles = snapshot();
                if (tiles.Count == 0)
                {
                    return false;
                }

                var path = $"{pathWithoutExtension}.stage-{index:D3}{extension}";

                // Stages are written long before the Worker saves the real output, so the output
                // folder may not exist yet.
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                using (progress.Begin($"Stage {index} from {tiles.Count:N0} tile(s)"))
                {
                    var assignment = Assign(
                        cellSignatures, tiles, columns, rows,
                        repeatAvoidanceRadius, maxTileReuse: 0, phase: null, cancellationToken);

                    using var mosaic = Render(assignment, tiles, columns, rows, tileSize);

                    if (colorAdjustStrength > 0f)
                    {
                        ApplyColorAdjust(mosaic, baseAtOutputScale, colorAdjustStrength);
                    }

                    await mosaic.SaveAsync(path, cancellationToken);
                }

                produced = true;
                logger.LogInformation(
                    "Stage {Index} written to {Path} from the first {Tiles:N0} tile(s).",
                    index, path, tiles.Count);

                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A preview is never worth failing a run that may already be minutes in.
                logger.LogWarning("Could not write an intermediate stage: {Reason}", ex.Message);
                return false;
            }
            finally
            {
                schedule.Release(clock.Elapsed, produced);
            }
        }
    }
}
