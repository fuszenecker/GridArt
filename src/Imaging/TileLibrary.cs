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

    /// <summary>Number of times this tile has been placed in the mosaic so far.</summary>
    public int UseCount { get; set; }

    public void Dispose() => Pixels.Dispose();
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
        IProgressReporter? progress = null)
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

        // Decoding dominates load time and is CPU-bound, so fan out across cores. The bag keeps the
        // parallel writes lock-free; results are re-sorted afterwards for deterministic output.
        var loaded = new System.Collections.Concurrent.ConcurrentBag<Tile>();
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
                            cache?.Set(file, image);
                        }

                        var signature = ColorSignature.Compute(image, signatureGrid);
                        loaded.Add(new Tile(path, image, signature));
                        image = null;
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
            });

        loadPhase.Dispose();

        if (cache is not null)
        {
            var decoded = loaded.Count - cacheHits;

            // Nothing new to store and nothing to prune is the common warm-cache case; skip the phase
            // entirely rather than announce a no-op.
            if (decoded > 0 || cache.LoadedCount != loaded.Count)
            {
                using var savePhase = progress.Begin("Saving tile cache", unit: "entries");
                cache.Save(loaded.Select(t => t.Path).ToArray());
                savePhase.Advance(loaded.Count);
            }
            else
            {
                cache.Save(loaded.Select(t => t.Path).ToArray());
            }
        }

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
