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
        CancellationToken cancellationToken)
    {
        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        var candidates = Directory
            .EnumerateFiles(folder, "*", searchOption)
            .Where(path => SupportedExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"No supported images found in '{folder}'. Supported extensions: " +
                string.Join(", ", SupportedExtensions.Order(StringComparer.OrdinalIgnoreCase)));
        }

        logger.LogInformation("Loading {Count} candidate tile images from {Folder}.", candidates.Length, folder);

        // Decoding dominates load time and is CPU-bound, so fan out across cores. The bag keeps the
        // parallel writes lock-free; results are re-sorted afterwards for deterministic output.
        var loaded = new System.Collections.Concurrent.ConcurrentBag<Tile>();
        var failures = 0;

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions { CancellationToken = cancellationToken },
            async (path, token) =>
            {
                try
                {
                    var image = await Image.LoadAsync<Rgba32>(path, token);
                    try
                    {
                        // "Max" plus centre crop preserves the source aspect ratio instead of
                        // squashing portraits into square cells.
                        image.Mutate(ctx => ctx.Resize(new ResizeOptions
                        {
                            Size = cellSize,
                            Mode = ResizeMode.Crop,
                            Position = AnchorPositionMode.Center,
                            Sampler = KnownResamplers.Lanczos3,
                        }));

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
            });

        var tiles = loaded.OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase).ToList();

        if (tiles.Count == 0)
        {
            throw new InvalidOperationException(
                $"None of the {candidates.Length} image(s) in '{folder}' could be decoded.");
        }

        logger.LogInformation(
            "Prepared {Loaded} tile(s) at {Width}x{Height}{Skipped}.",
            tiles.Count,
            cellSize.Width,
            cellSize.Height,
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
