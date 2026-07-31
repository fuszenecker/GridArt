using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace gridart.Imaging;

/// <summary>
/// On-disk cache of decoded-and-resized tile pixels.
/// </summary>
/// <remarks>
/// <para>
/// Decoding a folder of full-size photos and resampling each one to cell size is the dominant cost of
/// a run, and it is perfectly deterministic: the same file at the same <c>TileSize</c> always yields
/// the same cell-sized bitmap. That result is what gets cached.
/// </para>
/// <para>
/// Only the resized pixels are stored — <b>not</b> the colour signature. Fingerprinting a
/// cell-sized bitmap costs a few thousand pixel reads, so recomputing it every run is free, and it
/// keeps <c>SignatureGrid</c> out of the cache key entirely. Fewer key dimensions means fewer ways
/// for the cache to hand back something subtly wrong.
/// </para>
/// <para>
/// What the cached bitmap depends on, and therefore what the key covers:
/// the file's identity (path, length, last-write time), the target <c>TileSize</c>, and the
/// <see cref="FormatVersion"/> that stands in for the resize/crop algorithm. Options that only affect
/// how tiles are *chosen* or *composited* — <c>TilesAcross</c>, <c>SignatureGrid</c>,
/// <c>ColorAdjustStrength</c>, <c>MaxTileReuse</c>, <c>RepeatAvoidanceRadius</c>, the base image —
/// must never enter the key, or every run would miss for no reason.
/// </para>
/// <para>
/// A cache is an optimisation and never a source of failure: any error reading or writing it is
/// swallowed (logged at debug) and the run proceeds by decoding normally.
/// </para>
/// </remarks>
public sealed class TileCache
{
    private const uint Magic = 0x47524441; // "GRDA"

    /// <summary>
    /// Bump this whenever the produced pixels could change — the resize sampler, the crop mode, the
    /// pixel format, or this file's layout. Stale entries from an older algorithm are then ignored
    /// rather than silently reused.
    /// </summary>
    private const int FormatVersion = 1;

    // Concurrent because tiles are loaded in parallel: TryGet reads while other threads Set. A plain
    // Dictionary read concurrently with a write is undefined behaviour, not merely a lost update.
    private readonly ConcurrentDictionary<string, Entry> entries;
    private readonly string path;
    private readonly int tileSize;
    private readonly ILogger logger;

    private TileCache(string path, int tileSize, ConcurrentDictionary<string, Entry> entries, ILogger logger)
    {
        this.path = path;
        this.tileSize = tileSize;
        this.entries = entries;
        this.logger = logger;
    }

    /// <summary>Number of entries loaded from disk.</summary>
    public int LoadedCount { get; private set; }

    private readonly record struct Entry(long Length, long LastWriteUtcTicks, byte[] Pixels);

    /// <summary>
    /// Resolves the cache file for a tiles folder at a given tile size. Separate folders and tile
    /// sizes get separate files, so a run never rewrites another configuration's cache.
    /// </summary>
    public static string ResolveCachePath(string? cacheDirectory, string tilesFolder, int tileSize)
    {
        var directory = string.IsNullOrWhiteSpace(cacheDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GridArt",
                "cache")
            : Path.GetFullPath(cacheDirectory);

        // Windows paths are case-insensitive, so normalise before hashing or the same folder typed
        // with different casing would get two caches.
        var normalized = Path.GetFullPath(tilesFolder)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16];

        return Path.Combine(directory, $"tiles-{hash}-t{tileSize}-v{FormatVersion}.bin");
    }

    /// <summary>Opens the cache for a run, returning an empty cache when it is missing or unusable.</summary>
    public static TileCache Open(string? cacheDirectory, string tilesFolder, int tileSize, ILogger logger)
    {
        var cachePath = ResolveCachePath(cacheDirectory, tilesFolder, tileSize);
        var entries = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var cache = new TileCache(cachePath, tileSize, entries, logger);

        try
        {
            if (File.Exists(cachePath))
            {
                cache.Read(cachePath);
            }
        }
        catch (Exception ex)
        {
            // Truncated or corrupt cache: start clean rather than fail the run.
            entries.Clear();
            logger.LogDebug(ex, "Ignoring unreadable tile cache at {Path}.", cachePath);
        }

        cache.LoadedCount = entries.Count;
        return cache;
    }

    /// <summary>Deletes every cache file in the cache directory.</summary>
    public static int Clear(string? cacheDirectory, ILogger logger)
    {
        var probe = ResolveCachePath(cacheDirectory, ".", 1);
        var directory = Path.GetDirectoryName(probe)!;

        if (!Directory.Exists(directory))
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "tiles-*.bin"))
        {
            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Could not delete {Path}: {Reason}", file, ex.Message);
            }
        }

        return removed;
    }

    /// <summary>
    /// Returns the cached cell-sized image for <paramref name="file"/>, or null when absent or stale.
    /// </summary>
    /// <remarks>
    /// Staleness is judged by length plus last-write time — the same signal MSBuild and most build
    /// tools use. It is not content hashing: a file edited so that both its length and its timestamp
    /// are unchanged would go undetected. Hashing every source file would mean reading all the bytes
    /// off disk, which is most of what the cache exists to avoid. Use <c>--clear-cache</c> if a tile
    /// folder was rewritten in place.
    /// </remarks>
    public Image<Rgba32>? TryGet(FileInfo file)
    {
        if (!entries.TryGetValue(file.FullName, out var entry))
        {
            return null;
        }

        if (entry.Length != file.Length ||
            entry.LastWriteUtcTicks != file.LastWriteTimeUtc.Ticks)
        {
            return null;
        }

        try
        {
            return Image.LoadPixelData<Rgba32>(entry.Pixels, tileSize, tileSize);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Discarding malformed cache entry for {Path}.", file.FullName);
            return null;
        }
    }

    /// <summary>Records the resized pixels for a file that had to be decoded.</summary>
    public void Set(FileInfo file, Image<Rgba32> resized)
    {
        var pixels = new byte[tileSize * tileSize * 4];
        resized.CopyPixelDataTo(pixels);

        entries[file.FullName] = new Entry(file.Length, file.LastWriteTimeUtc.Ticks, pixels);
    }

    /// <summary>
    /// Writes the cache, keeping only <paramref name="livePaths"/> so entries for deleted files are
    /// pruned. Does nothing when the content is unchanged, to avoid rewriting megabytes each run.
    /// </summary>
    public void Save(IReadOnlyCollection<string> livePaths)
    {
        try
        {
            var live = new HashSet<string>(livePaths, StringComparer.OrdinalIgnoreCase);
            var stale = entries.Keys.Where(k => !live.Contains(k)).ToArray();

            foreach (var key in stale)
            {
                entries.TryRemove(key, out _);
            }

            if (stale.Length == 0 && entries.Count == LoadedCount)
            {
                return; // Nothing new and nothing pruned.
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            // Write to a temporary file and move it into place, so a crash or a concurrent run can
            // never leave a half-written cache behind.
            var temp = $"{path}.{Environment.ProcessId}.tmp";
            using (var stream = File.Create(temp))
            {
                Write(stream);
            }

            File.Move(temp, path, overwrite: true);
            logger.LogDebug("Saved {Count} tile cache entries to {Path}.", entries.Count, path);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not save the tile cache to {Path}.", path);
        }
    }

    private void Read(string cachePath)
    {
        using var stream = File.OpenRead(cachePath);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        if (reader.ReadUInt32() != Magic ||
            reader.ReadInt32() != FormatVersion ||
            reader.ReadInt32() != tileSize)
        {
            return;
        }

        var expected = tileSize * tileSize * 4;
        var count = reader.ReadInt32();

        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadString();
            var length = reader.ReadInt64();
            var ticks = reader.ReadInt64();
            var pixels = reader.ReadBytes(expected);

            if (pixels.Length != expected)
            {
                return; // Truncated: keep whatever parsed cleanly so far.
            }

            entries[key] = new Entry(length, ticks, pixels);
        }
    }

    private void Write(Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(tileSize);
        writer.Write(entries.Count);

        foreach (var (key, entry) in entries)
        {
            writer.Write(key);
            writer.Write(entry.Length);
            writer.Write(entry.LastWriteUtcTicks);
            writer.Write(entry.Pixels);
        }
    }
}
