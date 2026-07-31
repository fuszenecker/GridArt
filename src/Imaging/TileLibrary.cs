using gridart.Progress;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace gridart.Imaging;

/// <summary>One source image, already scaled to the mosaic's cell size and fingerprinted.</summary>
public sealed class Tile : IDisposable
{
    public Tile(string path, Image<Rgba32> pixels, ColorSignature signature)
    {
        Path = path;
        Pixels = pixels;
        Signature = signature;
    }

    /// <summary>Path of the source image, kept for diagnostics.</summary>
    public string Path { get; }

    /// <summary>The tile rendered at cell resolution, centre-cropped to the cell aspect ratio.</summary>
    public Image<Rgba32> Pixels { get; }

    public ColorSignature Signature { get; }

    public void Dispose() => Pixels.Dispose();
}

/// <summary>
/// Renders an intermediate mosaic from the tiles loaded so far. Implemented by
/// <c>MosaicBuilder.StageWriter</c>; the interface exists so tile loading can offer the hook without
/// knowing how a mosaic is built.
/// </summary>
public interface ITileStageWriter
{
    /// <summary>
    /// Writes a stage if one is due, and returns whether it did.
    /// </summary>
    /// <param name="snapshot">
    /// Produces the tiles available right now. Called only when a stage is actually due, so the cost of
    /// copying the list is not paid on every loaded tile.
    /// </param>
    Task<bool> TryWriteAsync(Func<IReadOnlyList<Tile>> snapshot, CancellationToken cancellationToken);
}

/// <summary>
/// Loads a folder of images into cell-sized <see cref="Tile"/> instances. Unreadable or unsupported
/// files are skipped with a warning rather than failing the whole run — tile folders are usually
/// scraped photo libraries that contain the odd stray file.
/// </summary>
public sealed class TileLibrary : IDisposable
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tga", ".tiff", ".tif", ".webp", ".pbm", ".qoi",
    };

    private readonly List<Tile> tiles;

    private TileLibrary(List<Tile> tiles) => this.tiles = tiles;

    public IReadOnlyList<Tile> Tiles => tiles;

    public static async Task<TileLibrary> LoadAsync(
        string folder,
        Size cellSize,
        int signatureGrid,
        bool recursive,
        ILogger logger,
        CancellationToken cancellationToken,
        TileCache? cache = null,
        IProgressReporter? progress = null,
        ITileStageWriter? stages = null)
    {
        progress ??= NullProgressReporter.Instance;

        // Scanning a large or networked folder tree is itself slow enough to need feedback, and the
        // total is unknown until it finishes.
        string[] candidates;
        using (var scan = progress.Begin("Scanning folder", unit: "files"))
        {
            var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var found = new List<string>();

            foreach (var path in Directory.EnumerateFiles(folder, "*", searchOption))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (SupportedExtensions.Contains(Path.GetExtension(path)))
                {
                    found.Add(path);
                    scan.Advance();
                }
            }

            candidates = found.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
        }

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"No supported images found in '{folder}'. Supported extensions: " +
                string.Join(", ", SupportedExtensions.Order(StringComparer.OrdinalIgnoreCase)));
        }

        logger.LogDebug("Loading {Count} candidate tile images from {Folder}.", candidates.Length, folder);

        // Decoding dominates load time and is CPU-bound, so fan out across cores. A plain List under a
        // lock rather than a ConcurrentBag: an intermediate stage needs a snapshot of what has loaded
        // so far, which a bag cannot give without enumerating it concurrently with the writes.
        var loaded = new List<Tile>(candidates.Length);
        var loadedLock = new Lock();
        var failures = 0;
        var cacheHits = 0;

        using var loadPhase = progress.Begin("Loading tiles", candidates.Length, "tiles");

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { CancellationToken = cancellationToken },
            async (path, token) =>
            {
                try
                {
                    var file = new FileInfo(path);

                    // A cache hit skips both the decode and the resample, which is the whole cost.
                    // The signature is always recomputed: it is cheap on a cell-sized image and
                    // keeps SignatureGrid out of the cache key.
                    var image = cache?.TryGet(file);
                    var fromCache = image is not null;

                    if (image is null)
                    {
                        image = await Image.LoadAsync<Rgba32>(path, token);

                        // Crop mode preserves the source aspect ratio instead of squashing
                        // portraits into square cells.
                        image.Mutate(ctx => ctx.Resize(new ResizeOptions
                        {
                            Size = cellSize,
                            Mode = ResizeMode.Crop,
                            Position = AnchorPositionMode.Center,
                            Sampler = KnownResamplers.Lanczos3,

                            // Compand converts to linear light before resampling and back after.
                            // Without it the resampler averages gamma-encoded bytes, which makes
                            // every downscaled tile darker than the photo it came from — measured at
                            // -0.10 mean linear luma over 300 tiles (sRGB grey 153 -> 128), and up to
                            // -0.26 on high-contrast ones. That is the brightness shift, and it is
                            // not optional: a tile must keep the colours of its source image.
                            Compand = true,
                        }));
                    }
                    else
                    {
                        Interlocked.Increment(ref cacheHits);
                    }

                    try
                    {
                        if (!fromCache)
                        {
                            // Appends straight to the cache file, so the decode survives an
                            // interrupted run instead of being thrown away.
                            cache?.Set(file, image);
                        }

                        var signature = ColorSignature.Compute(image, signatureGrid);
                        var tile = new Tile(path, image, signature);
                        image = null;

                        lock (loadedLock)
                        {
                            loaded.Add(tile);
                        }
                    }
                    finally
                    {
                        image?.Dispose();
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Interlocked.Increment(ref failures);
                    logger.LogWarning("Skipping {Path}: {Reason}", path, ex.Message);
                }
                finally
                {
                    loadPhase.Advance();
                }

                if (stages is not null)
                {
                    // Runs on this loader thread, so one core renders the preview while the rest keep
                    // decoding. The snapshot is only taken if a stage is actually due.
                    await stages.TryWriteAsync(
                        () =>
                        {
                            lock (loadedLock)
                            {
                                return loaded.ToArray();
                            }
                        },
                        token);
                }
            });

        loadPhase.Dispose();

        // Tiles were appended to the cache as they decoded; this only prunes deleted files and
        // compacts records superseded during the run, so it is usually a no-op.
        if (cache is not null)
        {
            using var savePhase = progress.Begin("Finalising tile cache", unit: "entries");
            if (cache.Save(loaded.Select(t => t.Path).ToArray()))
            {
                savePhase.Advance(loaded.Count);
            }
        }

        // Sorted so the finished mosaic is reproducible: tile order decides which of two equally good
        // candidates wins, and parallel loading finishes in a different order every run.
        var tiles = loaded.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();

        if (tiles.Count == 0)
        {
            throw new InvalidOperationException(
                $"None of the {candidates.Length} image(s) in '{folder}' could be decoded.");
        }

        logger.LogInformation(
            "Prepared {Loaded} tile(s) at {Width}x{Height}{Cached}{Skipped}.",
            tiles.Count,
            cellSize.Width,
            cellSize.Height,
            cache is null ? string.Empty : $", {cacheHits} from cache",
            failures == 0 ? string.Empty : $", skipped {failures}");

        return new TileLibrary(tiles);
    }

    public void Dispose()
    {
        foreach (var tile in tiles)
        {
            tile.Dispose();
        }
        tiles.Clear();
    }
}
