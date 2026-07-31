using System.Diagnostics;
using gridart.Progress;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
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
    /// Ceiling on the distance block buffer, in floats — 8 Mi floats is 32 MB, which comfortably fits
    /// an L3 cache-sized working set while keeping every core fed. The full cells × tiles matrix is
    /// never materialised: 100k cells × 30k tiles would be 12 GB.
    /// </summary>
    private const int DistanceBlockFloats = 8 * 1024 * 1024;

    /// <summary>
    /// Ceiling on the block in cells, so a tiny tile library does not produce an absurdly long block
    /// that the sequential pick then has to walk before the next block can be computed.
    /// </summary>
    private const int DistanceBlockCells = 4096;

    public async Task<MosaicResult> BuildAsync(
        MosaicOptions options,
        CancellationToken cancellationToken,
        IProgressReporter? progress = null)
    {
        progress ??= NullProgressReporter.Instance;
        var stopwatch = Stopwatch.StartNew();

        if (options.ClearCache)
        {
            var removed = TileCache.Clear(options.CacheDirectory, logger);
            logger.LogInformation("Cleared {Count} cache file(s).", removed);
        }

        // Tiles are cropped to the cell shape, which follows the base image's aspect ratio, so loading
        // cannot start until the base dimensions are known. Only the *dimensions* though — that is a
        // header read of a few hundred bytes, not a decode — so the expensive part of the base work
        // still overlaps loading, as below.
        Size baseSize;
        using (progress.Begin("Reading base image size"))
        {
            var info = await Image.IdentifyAsync(options.BaseImage, cancellationToken);
            baseSize = info.Size;
        }

        var (columns, rows) = ResolveGrid(baseSize, options.TilesAcross);
        var cellSize = ResolveTileSize(baseSize, options.TileSize);
        var cellCount = (long)columns * rows;

        logger.LogInformation(
            "Base image {Path} is {Width}x{Height}.", options.BaseImage, baseSize.Width, baseSize.Height);
        logger.LogInformation(
            "Grid {Columns}x{Rows} = {Cells} cells of {CellW}x{CellH}; output {OutWidth}x{OutHeight}.",
            columns, rows, cellCount, cellSize.Width, cellSize.Height,
            columns * cellSize.Width, rows * cellSize.Height);

        // Decoding the base image and computing its cell signatures is independent of loading the tiles,
        // and both are slow, so they run concurrently instead of one after the other. Started before the
        // cache is opened so that reading a multi-gigabyte cache file overlaps the base-image work too.
        var baseTask = AnalyseBaseAsync(options, columns, rows, cellSize, progress, cancellationToken);

        TileCache? cache = null;
        BaseAnalysis? analysis = null;
        TileLibrary? library = null;

        try
        {
            // Keyed on the tiles folder and the cell size — the things the cached pixels depend on.
            cache = options.NoCache
                ? null
                : TileCache.Open(options.CacheDirectory, options.TilesFolder, cellSize, logger);

            var stages = StageWriter.Create(options, baseTask, cellSize, logger, progress);

            var libraryTask = TileLibrary.LoadAsync(
                options.TilesFolder,
                cellSize,
                options.SignatureGrid,
                options.Recursive,
                logger,
                cancellationToken,
                cache,
                progress,
                stages);

            try
            {
                await Task.WhenAll(baseTask, libraryTask);
            }
            finally
            {
                // Whichever side finished still owns images even if the other threw, so both are
                // captured for the outer finally to dispose rather than leaked.
                if (baseTask.IsCompletedSuccessfully)
                {
                    analysis = baseTask.Result;
                }

                if (libraryTask.IsCompletedSuccessfully)
                {
                    library = libraryTask.Result;
                }
            }

            var tiles = library!.Tiles;

            if (options.MaxTileReuse > 0)
            {
                var capacity = (long)tiles.Count * options.MaxTileReuse;
                if (capacity < cellCount)
                {
                    // Phrased for the default case first, because that is the one people hit: "no reuse"
                    // simply needs one image per cell, and the fix is usually a smaller --tiles-across
                    // rather than more photos.
                    var limit = options.MaxTileReuse == 1
                        ? "each image is used at most once (--max-reuse 1, the default)"
                        : $"--max-reuse {options.MaxTileReuse}";

                    throw new InvalidOperationException(
                        $"A {columns}x{rows} grid needs {cellCount:N0} cells, but with {limit} the " +
                        $"{tiles.Count:N0} image(s) loaded can fill only {capacity:N0}. Add more images, " +
                        $"lower --tiles-across (about {LargestGridWithin(baseSize, tiles.Count, options.MaxTileReuse)} " +
                        "would fit), raise --max-reuse, or pass --max-reuse 0 to allow unlimited reuse.");
                }
            }

            // Checked before matching starts, not discovered halfway through: "no repetition" is a
            // requirement, so an impossible one fails immediately with the number of images that would
            // make it possible. The alternative — quietly shrinking the radius for the awkward cells —
            // is what made the old build produce repeats it had been told not to.
            var required = MinimumTilesForRepeatDistance(columns, rows, options.RepeatAvoidanceRadius);
            if (tiles.Count < required)
            {
                throw new InvalidOperationException(
                    $"--repeat-distance {options.RepeatAvoidanceRadius} needs at least {required:N0} " +
                    $"distinct image(s) for a {columns}x{rows} grid, but only {tiles.Count:N0} " +
                    "loaded. Add more images, lower --repeat-distance, or lower --tiles-across.");
            }

            int[] assignment;
            using (var phase = progress.Begin("Matching tiles", cellCount, "cells"))
            {
                // analysis! for the same reason as library! above: Task.WhenAll returned without
                // throwing, so both tasks completed and the finally assigned both.
                assignment = Assign(
                    analysis!.CellSignatures, tiles, columns, rows,
                    options.RepeatAvoidanceRadius, options.MaxTileReuse,
                    phase, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            Image<Rgba32> mosaic;
            using (var phase = progress.Begin("Rendering mosaic", cellCount, "tiles"))
            {
                mosaic = Render(assignment, tiles, columns, rows, cellSize, phase, cancellationToken);
            }

            try
            {
                if (options.ColorAdjustStrength > 0d)
                {
                    using var phase = progress.Begin("Colour matching", mosaic.Height, "rows");
                    ApplyColorAdjust(
                        mosaic, analysis.BaseAtOutputScale, (float)options.ColorAdjustStrength,
                        phase, cancellationToken);
                }

                MosaicQuality quality;
                using (var phase = progress.Begin("Scoring likeness", cellCount * 2, "cells"))
                {
                    quality = await MeasureAsync(
                        mosaic, analysis.BaseImage, columns, rows, options.SignatureGrid, assignment,
                        phase, cancellationToken);
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
        finally
        {
            library?.Dispose();
            analysis?.Dispose();
            cache?.Dispose();
        }
    }

    /// <summary>
    /// Everything derived from the base image alone: the grid it implies, a copy resampled to the exact
    /// output size for colour matching, and the per-cell signatures matching needs.
    /// </summary>
    private sealed record BaseAnalysis(
        Image<Rgba32> BaseImage,
        Image<Rgba32> BaseAtOutputScale,
        int Columns,
        int Rows,
        ColorSignature[] CellSignatures) : IDisposable
    {
        public void Dispose()
        {
            BaseImage.Dispose();
            BaseAtOutputScale.Dispose();
        }
    }

    private async Task<BaseAnalysis> AnalyseBaseAsync(
        MosaicOptions options,
        int columns,
        int rows,
        Size cellSize,
        IProgressReporter progress,
        CancellationToken cancellationToken)
    {
        Image<Rgba32> baseImage;
        using (progress.Begin("Reading base image"))
        {
            baseImage = await Image.LoadAsync<Rgba32>(options.BaseImage, cancellationToken);
        }

        try
        {
            var outputSize = new Size(columns * cellSize.Width, rows * cellSize.Height);

            // The base image is resampled to the exact output size once; the per-pixel colour-adjust
            // step then has a matching target pixel for every mosaic pixel, so tinting can follow
            // gradients inside a cell instead of pushing the whole cell toward one flat colour.
            Image<Rgba32> baseAtOutputScale;
            using (progress.Begin("Rescaling base image"))
            {
                baseAtOutputScale = baseImage.Clone(ctx => ctx.Resize(new ResizeOptions
                {
                    Size = outputSize,
                    Mode = ResizeMode.Stretch,
                    Sampler = KnownResamplers.Lanczos3,

                    // Resample in linear light; see the note on the tile resize in TileLibrary for what
                    // averaging gamma-encoded bytes does to brightness.
                    Compand = true,
                }));
            }

            try
            {
                ColorSignature[] cellSignatures;
                using (var phase = progress.Begin("Analysing base image", (long)columns * rows, "cells"))
                {
                    cellSignatures = ComputeCellSignatures(
                        baseImage, columns, rows, options.SignatureGrid, phase, cancellationToken);
                }

                return new BaseAnalysis(baseImage, baseAtOutputScale, columns, rows, cellSignatures);
            }
            catch
            {
                baseAtOutputScale.Dispose();
                throw;
            }
        }
        catch
        {
            baseImage.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The cell grid: <paramref name="tilesAcross"/> tiles on each axis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The grid is square and the cells are not.</b> That is the only way to give each tile the base
    /// image's aspect ratio, and the arithmetic forces the choice: the output measures
    /// <c>columns · cellWidth</c> by <c>rows · cellHeight</c>, so demanding both that the output match
    /// the base ratio and that <c>cellWidth : cellHeight</c> match it too leaves
    /// <c>columns : rows == 1 : 1</c>. Aspect ratio lives in the cell shape, not in the cell counts.
    /// </para>
    /// <para>
    /// It used to be the other way round: a proportional grid of square cells, which also produced a
    /// correctly-proportioned output — but every source photo was then centre-cropped to a square,
    /// throwing away the sides of every landscape shot and the top and bottom of every portrait one. The
    /// mosaic was right in outline and wrong in every tile.
    /// </para>
    /// </remarks>
    internal static (int Columns, int Rows) ResolveGrid(Size baseSize, int tilesAcross) =>
        (tilesAcross, tilesAcross);

    /// <summary>
    /// Shape of one cell: <paramref name="tileSize"/> on the longer edge, the shorter edge scaled to the
    /// base image's aspect ratio, so a tile is a miniature of the base image's shape.
    /// </summary>
    /// <remarks>
    /// On a 4:3 base a 4:3 photo now fits with no crop at all, where a square cell cost it a quarter of
    /// its width. Taken straight from the base dimensions rather than from the grid: with a square grid
    /// the cell ratio *is* the base ratio, and going via the grid would only reintroduce rounding.
    /// The short edge is clamped to 1px so an extreme panorama cannot round it to zero.
    /// </remarks>
    internal static Size ResolveTileSize(Size baseSize, int tileSize)
    {
        var ratio = (double)baseSize.Width / baseSize.Height;

        return ratio >= 1d
            ? new Size(tileSize, Math.Max(1, (int)Math.Round(tileSize / ratio)))
            : new Size(Math.Max(1, (int)Math.Round(tileSize * ratio)), tileSize);
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

        // Cells are independent and each writes only its own slot, so rows go across all cores.
        Parallel.For(0, rows, CpuBound(cancellationToken), row =>
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

            // Reported per row rather than per cell: a row is a coarse enough unit to keep the
            // counter cheap, and fine enough to animate on any realistic grid.
            phase?.Advance(columns);
        });

        return signatures;
    }

    /// <summary>
    /// Options for the parallel loops here. All of them are CPU-bound pixel work, so each is allowed
    /// the whole machine.
    /// </summary>
    private static System.Threading.Tasks.ParallelOptions CpuBound(CancellationToken cancellationToken) => new()
    {
        CancellationToken = cancellationToken,
        MaxDegreeOfParallelism = Environment.ProcessorCount,
    };

    /// <summary>
    /// Greedy nearest-signature assignment with reuse caps and local repeat avoidance. Cells are
    /// visited in a fixed order, so a run is reproducible for the same inputs and options.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b><c>repeatAvoidanceRadius</c> is a hard exclusion, not a preference.</b> A tile already placed
    /// within the radius is removed from the candidate set outright. It was previously a score penalty,
    /// which meant a repeat still won whenever every alternative scored worse than the penalty — so
    /// <c>-d 1</c> visibly placed a tile next to itself despite being asked not to.
    /// </para>
    /// <para>
    /// <b>It is never relaxed.</b> An earlier version shrank the radius for a cell that had no legal
    /// candidate and merely logged a warning, which meant "no repetition" still produced repetitions.
    /// A stated limit is either honoured or the run fails saying why — see
    /// <see cref="MinimumTilesForRepeatDistance"/>, which makes the failure predictable up front rather
    /// than a surprise thrown from the middle of matching.
    /// </para>
    /// <para>
    /// Use counts are local to the call rather than stored on <see cref="Tile"/>. That keeps the method
    /// free of side effects on the library, which is what lets an intermediate stage render from the
    /// same tiles without perturbing the final mosaic.
    /// </para>
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
        var total = columns * rows;
        var assignment = new int[total];
        Array.Fill(assignment, -1);

        var tileCount = tiles.Count;
        var useCounts = new int[tileCount];
        var reuseCap = maxTileReuse == 0 ? int.MaxValue : maxTileReuse;

        // Signatures are pulled out of the Tile objects once: the inner loop then walks a flat array
        // instead of dereferencing through an IReadOnlyList on every one of cells × tiles iterations.
        var signatures = new ColorSignature[tileCount];
        for (var t = 0; t < tileCount; t++)
        {
            signatures[t] = tiles[t].Signature;
        }

        // A cell's distance to every tile does not depend on what any other cell chose, so a block of
        // cells is measured across all cores and only the pick itself stays sequential — the pick has to
        // be, because the reuse cap and the repeat distance depend on the cells already placed.
        // Blocked rather than all-at-once so the buffer stays bounded: the full cells × tiles matrix
        // would be hundreds of gigabytes on a large run.
        var blockCells = Math.Clamp(DistanceBlockFloats / Math.Max(1, tileCount), 1, DistanceBlockCells);
        var distances = new float[blockCells * tileCount];
        var parallelOptions = CpuBound(cancellationToken);

        for (var start = 0; start < total; start += blockCells)
        {
            var count = Math.Min(blockCells, total - start);

            Parallel.For(0, count, parallelOptions, i =>
            {
                var cell = cellSignatures[start + i];
                var offset = i * tileCount;

                for (var t = 0; t < tileCount; t++)
                {
                    distances[offset + t] = cell.DistanceTo(signatures[t]);
                }
            });

            for (var i = 0; i < count; i++)
            {
                var cellIndex = start + i;
                var col = cellIndex % columns;
                var row = cellIndex / columns;
                var offset = i * tileCount;

                var bestScore = float.PositiveInfinity;
                var bestTile = -1;

                for (var t = 0; t < tileCount; t++)
                {
                    if (useCounts[t] >= reuseCap)
                    {
                        continue;
                    }

                    var score = distances[offset + t];
                    if (score >= bestScore)
                    {
                        // Already losing on colour alone; skip the neighbour scan. Strict '<' also
                        // makes the lowest tile index win a tie, which keeps a run reproducible.
                        continue;
                    }

                    if (repeatAvoidanceRadius > 0 &&
                        IsUsedNearby(assignment, columns, col, row, repeatAvoidanceRadius, t))
                    {
                        continue; // Excluded outright — this is what makes -d mean what it says.
                    }

                    bestScore = score;
                    bestTile = t;
                }

                if (bestTile < 0)
                {
                    // No fallback and no relaxation: honouring the radius is the whole point, and the
                    // caller checked MinimumTilesForRepeatDistance before starting, so reaching this is
                    // a reuse-cap exhaustion or a bug — either way it must not silently place a repeat.
                    throw new InvalidOperationException(
                        $"No image can fill cell ({col}, {row}) while honouring --repeat-distance " +
                        $"{repeatAvoidanceRadius}" +
                        (maxTileReuse > 0 ? $" and --max-reuse {maxTileReuse}" : string.Empty) +
                        $" with {tileCount} image(s). Add more images, lower --repeat-distance, " +
                        "or lower --tiles-across.");
                }

                assignment[cellIndex] = bestTile;
                useCounts[bestTile]++;
            }

            phase?.Advance(count);
        }

        return assignment;
    }

    /// <summary>
    /// How many distinct images are needed before a repeat distance is guaranteed satisfiable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Cells are filled in raster order, so when a cell is picked the only tiles banned for it are the
    /// already-assigned ones inside the radius: the <c>radius</c> full rows above (each contributing
    /// <c>2·radius + 1</c> cells, clamped to the grid width) plus the <c>radius</c> cells to its left.
    /// That count is the largest possible ban list, so one more image than that always leaves a legal
    /// choice — whatever the base image looks like and however the earlier cells fell.
    /// </para>
    /// <para>
    /// This is why the radius never has to be relaxed: the run can tell before matching starts whether
    /// the request is achievable, and say so with a number the user can act on, instead of discovering
    /// it at cell 40,000 and quietly placing a duplicate.
    /// </para>
    /// </remarks>
    internal static long MinimumTilesForRepeatDistance(int columns, int rows, int radius)
    {
        if (radius <= 0)
        {
            return 1;
        }

        var rowsAbove = Math.Min(radius, rows - 1);
        var perRow = Math.Min(columns, 2L * radius + 1);
        var leftOfCell = Math.Min(radius, columns - 1);

        return rowsAbove * perRow + leftOfCell + 1;
    }

    /// <summary>
    /// Largest <c>--tiles-across</c> whose grid still fits in the images available, so the error can
    /// name a value that works instead of only saying "lower it".
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="ResolveGrid"/> rather than computing a square root directly, so it stays
    /// correct if the grid shape ever changes again: the number printed in an error must be one that
    /// actually works on the next run.
    /// </remarks>
    internal static int LargestGridWithin(Size baseSize, int tileCount, int maxTileReuse)
    {
        var capacity = maxTileReuse <= 0 ? long.MaxValue : (long)tileCount * maxTileReuse;

        // Start from the square root of the capacity — the answer for a square grid — and walk down.
        // Bounded and cheap; a linear scan from tileCount would be up to 4096 iterations for nothing.
        var start = Math.Clamp((int)Math.Sqrt(capacity) + 1, 1, 4096);

        for (var across = start; across >= 1; across--)
        {
            var (c, r) = ResolveGrid(baseSize, across);
            if ((long)c * r <= capacity)
            {
                return across;
            }
        }

        return 1;
    }

    /// <summary>
    /// Whether <paramref name="tileIndex"/> already sits within <paramref name="radius"/> cells of
    /// (<paramref name="col"/>, <paramref name="row"/>).
    /// </summary>
    /// <remarks>
    /// Only already-assigned cells are scanned — the rows above plus the current row up to the previous
    /// column — because cells later in raster order hold no tile yet. Combined with the exclusion in
    /// <see cref="Assign"/>, that is still symmetric in effect: if A excludes B as its neighbour, B was
    /// placed first and A is the one that had to move, so no two cells within the radius end up equal.
    /// </remarks>
    private static bool IsUsedNearby(
        int[] assignment, int columns, int col, int row, int radius, int tileIndex)
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

    /// <summary>
    /// Copies the chosen tiles into the output. Cell rows write to disjoint pixel rows, so they are
    /// filled across all cores.
    /// </summary>
    /// <remarks>
    /// Pixel rows are copied verbatim rather than composited with <c>DrawImage</c>. A copy is both
    /// faster and exactly faithful: <c>DrawImage</c> blends the tile against the blank canvas, which
    /// rewrites the colour channels of a fully transparent source pixel to zero. A tile must reach the
    /// output with the colours it was decoded with, so nothing here touches a pixel's value.
    /// </remarks>
    private static Image<Rgba32> Render(
        int[] assignment,
        IReadOnlyList<Tile> tiles,
        int columns,
        int rows,
        Size cellSize,
        IProgressPhase? phase = null,
        CancellationToken cancellationToken = default)
    {
        var mosaic = new Image<Rgba32>(columns * cellSize.Width, rows * cellSize.Height);
        try
        {
            Parallel.For(0, rows, CpuBound(cancellationToken), row =>
            {
                for (var line = 0; line < cellSize.Height; line++)
                {
                    var target = mosaic.Frames.RootFrame
                        .DangerousGetPixelRowMemory(row * cellSize.Height + line).Span;

                    for (var col = 0; col < columns; col++)
                    {
                        var tile = tiles[assignment[row * columns + col]];
                        tile.Pixels.Frames.RootFrame.DangerousGetPixelRowMemory(line).Span
                            .CopyTo(target.Slice(col * cellSize.Width, cellSize.Width));
                    }
                }

                phase?.Advance(columns);
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
        IProgressPhase? phase = null,
        CancellationToken cancellationToken = default)
    {
        Debug.Assert(mosaic.Size == baseAtOutputScale.Size, "Colour-adjust target must match the mosaic size.");

        var keep = 1f - strength;

        Parallel.For(0, mosaic.Height, CpuBound(cancellationToken), y =>
        {
            var mosaicRow = mosaic.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;
            var targetRow = baseAtOutputScale.Frames.RootFrame.DangerousGetPixelRowMemory(y).Span;

            for (var x = 0; x < mosaicRow.Length; x++)
            {
                var m = mosaicRow[x];
                var t = targetRow[x];

                mosaicRow[x] = new Rgba32(
                    ColorMath.LinearToSrgb(ColorMath.SrgbToLinear(m.R) * keep + ColorMath.SrgbToLinear(t.R) * strength),
                    ColorMath.LinearToSrgb(ColorMath.SrgbToLinear(m.G) * keep + ColorMath.SrgbToLinear(t.G) * strength),
                    ColorMath.LinearToSrgb(ColorMath.SrgbToLinear(m.B) * keep + ColorMath.SrgbToLinear(t.B) * strength),

                    // The tile's own alpha, not an unconditional 255. Forcing opaque here turned a
                    // transparent PNG tile into a solid block, which is a colour change nobody asked
                    // for; only the colour channels are being blended.
                    m.A);
            }

            phase?.Advance();
        });
    }

    /// <summary>
    /// Scores the finished mosaic against the base image cell by cell. Comparing per cell rather
    /// than per pixel is the point: it measures the likeness that survives zooming out, which is
    /// exactly what a photomosaic is supposed to preserve.
    /// </summary>
    private static async Task<MosaicQuality> MeasureAsync(
        Image<Rgba32> mosaic,
        Image<Rgba32> baseImage,
        int columns,
        int rows,
        int signatureGrid,
        int[] assignment,
        IProgressPhase? phase = null,
        CancellationToken cancellationToken = default)
    {
        // Two passes over the grid, which is why the phase total is 2x the cell count. They read two
        // different images and share nothing, so they run at the same time; each is internally parallel
        // as well, and the pair simply shares the cores.
        var baseTask = Task.Run(
            () => ComputeCellSignatures(baseImage, columns, rows, signatureGrid, phase, cancellationToken),
            cancellationToken);
        var mosaicTask = Task.Run(
            () => ComputeCellSignatures(mosaic, columns, rows, signatureGrid, phase, cancellationToken),
            cancellationToken);

        var baseCells = await baseTask;
        var mosaicCells = await mosaicTask;

        var deltas = new double[baseCells.Length];

        Parallel.For(0, baseCells.Length, CpuBound(cancellationToken), i =>
        {
            // DistanceTo is a mean of squared ΔE values, so the square root is a ΔE again.
            deltas[i] = Math.Sqrt(baseCells[i].DistanceTo(mosaicCells[i]));
        });

        var total = 0d;
        foreach (var delta in deltas)
        {
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
    /// <item>likeness is not scored — that is two extra passes over the grid for a throwaway image;</item>
    /// <item>a stage is skipped when too few tiles have loaded to satisfy <c>--max-reuse</c>, rather than
    /// quietly ignoring the cap: the options are instructions, and a preview that breaks one would
    /// misrepresent what the run is doing;</item>
    /// <item>tiles arrive in load order rather than sorted order, so stages are not reproducible.
    /// The final mosaic is, and nothing here touches it: <see cref="Assign"/> keeps its use counts
    /// local, and the tile pixels are only read.</item>
    /// </list>
    /// </remarks>
    private sealed class StageWriter : ITileStageWriter
    {
        private readonly StageSchedule schedule;
        private readonly Task<BaseAnalysis> baseTask;
        private readonly Size cellSize;
        private readonly float colorAdjustStrength;
        private readonly int repeatAvoidanceRadius;
        private readonly int maxTileReuse;
        private readonly string pathWithoutExtension;
        private readonly string extension;
        private readonly ILogger logger;
        private readonly IProgressReporter progress;

        private StageWriter(
            MosaicOptions options,
            Task<BaseAnalysis> baseTask,
            Size cellSize,
            ILogger logger,
            IProgressReporter progress)
        {
            schedule = new StageSchedule(TimeSpan.FromSeconds(options.StageIntervalSeconds));
            this.baseTask = baseTask;
            this.cellSize = cellSize;
            colorAdjustStrength = (float)options.ColorAdjustStrength;
            repeatAvoidanceRadius = options.RepeatAvoidanceRadius;
            maxTileReuse = options.MaxTileReuse;
            this.logger = logger;
            this.progress = progress;

            var output = options.ResolveOutputPath();
            extension = Path.GetExtension(output) is { Length: > 0 } ext ? ext : ".png";
            pathWithoutExtension = Path.Combine(
                Path.GetDirectoryName(output) ?? ".",
                Path.GetFileNameWithoutExtension(output));
        }

        /// <summary>Returns null when stages are switched off, so the load loop skips the check entirely.</summary>
        /// <param name="baseTask">
        /// The base-image analysis, which now runs concurrently with tile loading and so may still be in
        /// flight when a stage falls due. Passed as the task rather than its result because the writer is
        /// created before it completes; it is awaited only under a stage claim.
        /// </param>
        public static ITileStageWriter? Create(
            MosaicOptions options,
            Task<BaseAnalysis> baseTask,
            Size cellSize,
            ILogger logger,
            IProgressReporter progress)
        {
            if (options.StageIntervalSeconds <= 0)
            {
                return null;
            }

            return new StageWriter(options, baseTask, cellSize, logger, progress);
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
                // There is nothing to draw against until the base image has been analysed, which now runs
                // concurrently with loading and can still be in flight. Awaited inside the claim rather
                // than checked before it: the claim is exclusive, so at most one loader slot ever parks
                // here, and only until the analysis lands. Checking-and-skipping instead would drop every
                // early stage, which on a small run means no previews at all.
                var analysis = await baseTask;
                var tiles = snapshot();
                if (tiles.Count == 0)
                {
                    return false;
                }

                // The limits are limits for previews too. Early on there are genuinely too few tiles to
                // fill the grid within the reuse cap or to honour the repeat distance; that is a reason
                // to wait for the next stage, not to break the rule and not to fail the run.
                if (maxTileReuse > 0 &&
                    (long)tiles.Count * maxTileReuse < (long)analysis.Columns * analysis.Rows)
                {
                    return false;
                }

                if (tiles.Count < MinimumTilesForRepeatDistance(
                        analysis.Columns, analysis.Rows, repeatAvoidanceRadius))
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
                        analysis.CellSignatures, tiles, analysis.Columns, analysis.Rows,
                        repeatAvoidanceRadius, maxTileReuse,
                        phase: null, cancellationToken);

                    using var mosaic = Render(
                        assignment, tiles, analysis.Columns, analysis.Rows, cellSize,
                        phase: null, cancellationToken);

                    if (colorAdjustStrength > 0f)
                    {
                        ApplyColorAdjust(
                            mosaic, analysis.BaseAtOutputScale, colorAdjustStrength,
                            phase: null, cancellationToken);
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
