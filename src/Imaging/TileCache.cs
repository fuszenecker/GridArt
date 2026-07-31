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
/// <b>Entries are appended as each tile is decoded, not collected and written at the end.</b> With tens
/// of thousands of images a cold run takes many minutes, and a single write at the end means a run
/// interrupted at minute nine — Ctrl-C, a crash, a full disk, a laptop lid — leaves nothing behind and
/// starts from zero next time. The file format is therefore a plain record stream with no count in the
/// header, so a new entry costs one append instead of a full rewrite. A resumed run picks up exactly
/// where the previous one was killed.
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
public sealed class TileCache : IDisposable
{
    private const uint Magic = 0x47524441; // "GRDA"

    /// <summary>
    /// Starts every record. Appending is not atomic, so a killed process can leave a half-written
    /// record at the end of the file; the marker lets the reader recognise the tail as garbage
    /// instead of interpreting whatever bytes follow as a string length.
    /// </summary>
    private const uint RecordMarker = 0x54494C45; // "TILE"

    /// <summary>
    /// Bump this whenever the produced pixels could change — the resize sampler, the crop mode, the
    /// pixel format, or this file's layout. Stale entries from an older algorithm are then ignored
    /// rather than silently reused.
    /// </summary>
    /// <remarks>
    /// v2 dropped the entry count from the header and added <see cref="RecordMarker"/>, so entries can
    /// be appended one at a time while tiles load. v3 stores a cell width <i>and</i> height rather than
    /// one square size: cells now take the base image's aspect ratio, so cached square pixels from v2
    /// are the wrong shape and must not be reused.
    /// </remarks>
    private const int FormatVersion = 3;

    // Concurrent because tiles are loaded in parallel: TryGet reads while other threads Set. A plain
    // Dictionary read concurrently with a write is undefined behaviour, not merely a lost update.
    private readonly ConcurrentDictionary<string, Entry> entries;
    private readonly string path;
    private readonly Size cellSize;
    private readonly ILogger logger;

    // Serialises everything that touches the file. Appends happen from the parallel load loop, so the
    // writes themselves must not interleave; the lock is held only for one 4 KB-ish record while the
    // expensive decoding continues on the other threads.
    private readonly Lock fileLock = new();

    private FileStream? appendStream;
    private BinaryWriter? appendWriter;

    /// <summary>Length of the last complete record, i.e. where a torn tail begins.</summary>
    private long validLength;
    private bool appendsUsable = true;
    private int appended;
    private int superseded;

    private TileCache(string path, Size cellSize, ConcurrentDictionary<string, Entry> entries, ILogger logger)
    {
        this.path = path;
        this.cellSize = cellSize;
        this.entries = entries;
        this.logger = logger;
    }

    /// <summary>Number of entries loaded from disk.</summary>
    public int LoadedCount { get; private set; }

    /// <summary>Number of entries appended during this run.</summary>
    public int AppendedCount => Volatile.Read(ref appended);

    private readonly record struct Entry(long Length, long LastWriteUtcTicks, byte[] Pixels);

    /// <summary>
    /// Resolves the cache file for a tiles folder at a given tile size. Separate folders and tile
    /// sizes get separate files, so a run never rewrites another configuration's cache.
    /// </summary>
    public static string ResolveCachePath(string? cacheDirectory, string tilesFolder, Size cellSize)
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

        return Path.Combine(
            directory, $"tiles-{hash}-t{cellSize.Width}x{cellSize.Height}-v{FormatVersion}.bin");
    }

    /// <summary>Opens the cache for a run, returning an empty cache when it is missing or unusable.</summary>
    public static TileCache Open(string? cacheDirectory, string tilesFolder, Size cellSize, ILogger logger)
    {
        var cachePath = ResolveCachePath(cacheDirectory, tilesFolder, cellSize);
        var entries = new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var cache = new TileCache(cachePath, cellSize, entries, logger);

        try
        {
            if (File.Exists(cachePath))
            {
                cache.validLength = cache.Read(cachePath);
            }
        }
        catch (Exception ex)
        {
            // Truncated or corrupt cache: start clean rather than fail the run.
            entries.Clear();
            cache.validLength = 0;
            logger.LogDebug(ex, "Ignoring unreadable tile cache at {Path}.", cachePath);
        }

        cache.LoadedCount = entries.Count;
        return cache;
    }

    /// <summary>Deletes every cache file in the cache directory.</summary>
    public static int Clear(string? cacheDirectory, ILogger logger)
    {
        var probe = ResolveCachePath(cacheDirectory, ".", new Size(1, 1));
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
            return Image.LoadPixelData<Rgba32>(entry.Pixels, cellSize.Width, cellSize.Height);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Discarding malformed cache entry for {Path}.", file.FullName);
            return null;
        }
    }

    /// <summary>
    /// Records the resized pixels for a file that had to be decoded, and appends them to the cache
    /// file immediately so the work survives an interrupted run.
    /// </summary>
    public void Set(FileInfo file, Image<Rgba32> resized)
    {
        var pixels = new byte[cellSize.Width * cellSize.Height * 4];
        resized.CopyPixelDataTo(pixels);

        var entry = new Entry(file.Length, file.LastWriteTimeUtc.Ticks, pixels);

        if (!entries.TryAdd(file.FullName, entry))
        {
            // Re-decoded because the old entry was stale. The superseded record is still on disk;
            // last one wins on read, and the end-of-run save compacts the file.
            entries[file.FullName] = entry;
            Interlocked.Increment(ref superseded);
        }

        Append(file.FullName, entry);
    }

    /// <summary>
    /// Finalises the cache: prunes entries whose files are gone and compacts records superseded during
    /// the run. Returns true if the file was rewritten.
    /// </summary>
    /// <remarks>
    /// Because <see cref="Set"/> already appended every new entry, this is normally a no-op that reads
    /// and writes nothing — the multi-megabyte write it used to perform now happens incrementally
    /// during loading.
    /// </remarks>
    public bool Save(IReadOnlyCollection<string> livePaths)
    {
        lock (fileLock)
        {
            try
            {
                // Expanded to full paths before comparing. Entries are keyed on FileInfo.FullName, but
                // the live list holds the paths as they were enumerated, which are relative whenever the
                // tiles folder was given as a relative argument — `gridart base.png tiles`. Comparing the
                // two forms directly made every entry look stale, so a run pruned the whole cache and
                // rewrote it empty: the cache never once hit.
                var live = new HashSet<string>(
                    livePaths.Select(static p => Path.GetFullPath(p)), StringComparer.OrdinalIgnoreCase);
                var stale = entries.Keys.Where(k => !live.Contains(k)).ToArray();

                foreach (var key in stale)
                {
                    entries.TryRemove(key, out _);
                }

                // Nothing to prune, nothing shadowed by a newer record, and every append landed:
                // the file on disk already says exactly this. Rewriting megabytes would be pure cost.
                if (stale.Length == 0 && Volatile.Read(ref superseded) == 0 && appendsUsable)
                {
                    return false;
                }

                CloseAppendStream();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                // Write to a temporary file and move it into place, so a crash or a concurrent run can
                // never leave a half-written cache behind.
                var temp = $"{path}.{Environment.ProcessId}.tmp";
                using (var stream = File.Create(temp))
                {
                    Write(stream);
                }

                File.Move(temp, path, overwrite: true);
                validLength = new FileInfo(path).Length;
                Interlocked.Exchange(ref superseded, 0);
                appendsUsable = true;

                logger.LogDebug("Rewrote the tile cache at {Path} with {Count} entries.", path, entries.Count);
                return true;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not save the tile cache to {Path}.", path);
                return false;
            }
        }
    }

    public void Dispose()
    {
        lock (fileLock)
        {
            CloseAppendStream();
        }
    }

    private void Append(string key, in Entry entry)
    {
        lock (fileLock)
        {
            if (!appendsUsable)
            {
                return;
            }

            try
            {
                if (appendWriter is null)
                {
                    OpenForAppend();
                }

                WriteRecord(appendWriter!, key, entry);

                // Flush the managed buffer per record so a killed process loses at most the record in
                // flight. Not Flush(true): forcing the platter on every tile would cost more than the
                // decode it is protecting, and a torn tail is already recoverable.
                appendWriter!.Flush();

                validLength = appendStream!.Position;
                Interlocked.Increment(ref appended);
            }
            catch (Exception ex)
            {
                // A read-only cache directory, a full disk, or another process holding the file. The
                // run continues in memory; the end-of-run Save then attempts one full write.
                appendsUsable = false;
                CloseAppendStream();
                logger.LogDebug(ex, "Could not append to the tile cache at {Path}.", path);
            }
        }
    }

    private void OpenForAppend()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        // FileShare.Read: a second gridart process must not append into the same file concurrently.
        // It will fail to open, log at debug, and fall back to writing the whole cache at the end.
        var stream = new FileStream(
            path,
            validLength > 0 ? FileMode.OpenOrCreate : FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.Read);

        var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        try
        {
            if (validLength > 0)
            {
                // Drop any half-written record left by a killed run before appending after it.
                stream.SetLength(validLength);
                stream.Seek(0, SeekOrigin.End);
            }
            else
            {
                WriteHeader(writer);
            }

            appendStream = stream;
            appendWriter = writer;
        }
        catch
        {
            writer.Dispose();
            stream.Dispose();
            throw;
        }
    }

    private void CloseAppendStream()
    {
        appendWriter?.Dispose();
        appendStream?.Dispose();
        appendWriter = null;
        appendStream = null;
    }

    /// <summary>
    /// Reads every complete record and returns the offset just past the last one, so a torn tail can
    /// be trimmed before the next append.
    /// </summary>
    private long Read(string cachePath)
    {
        // FileShare.ReadWrite, not File.OpenRead: another gridart process may be appending to this
        // file right now, and a plain read-share request would collide with its write handle and be
        // treated as an unusable cache.
        using var stream = new FileStream(
            cachePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream, Encoding.UTF8);

        if (stream.Length < 16 ||
            reader.ReadUInt32() != Magic ||
            reader.ReadInt32() != FormatVersion ||
            reader.ReadInt32() != cellSize.Width ||
            reader.ReadInt32() != cellSize.Height)
        {
            return 0;
        }

        var expected = cellSize.Width * cellSize.Height * 4;
        var good = stream.Position;

        while (stream.Position < stream.Length)
        {
            try
            {
                if (reader.ReadUInt32() != RecordMarker)
                {
                    break; // Garbage from an interrupted append; everything before it is still good.
                }

                var key = reader.ReadString();
                var length = reader.ReadInt64();
                var ticks = reader.ReadInt64();
                var pixels = reader.ReadBytes(expected);

                if (pixels.Length != expected)
                {
                    break; // Truncated final record.
                }

                entries[key] = new Entry(length, ticks, pixels);
                good = stream.Position;
            }
            catch (EndOfStreamException)
            {
                break;
            }
        }

        if (good < stream.Length)
        {
            logger.LogDebug(
                "Tile cache {Path} has {Bytes} trailing byte(s) from an interrupted run; they will be dropped.",
                cachePath, stream.Length - good);
        }

        return good;
    }

    private void Write(Stream stream)
    {
        using var writer = new BinaryWriter(stream, Encoding.UTF8);

        WriteHeader(writer);

        foreach (var (key, entry) in entries)
        {
            WriteRecord(writer, key, entry);
        }
    }

    private void WriteHeader(BinaryWriter writer)
    {
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(cellSize.Width);
        writer.Write(cellSize.Height);
        writer.Flush();
    }

    private static void WriteRecord(BinaryWriter writer, string key, in Entry entry)
    {
        writer.Write(RecordMarker);
        writer.Write(key);
        writer.Write(entry.Length);
        writer.Write(entry.LastWriteUtcTicks);
        writer.Write(entry.Pixels);
    }
}
