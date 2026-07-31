using gridart;
using gridart.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace gridart.Tests;

/// <summary>
/// Covers the two things that make a run over tens of thousands of images survivable: the cache is
/// written as tiles decode rather than at the end, and intermediate stage images show the mosaic
/// developing instead of leaving the run silent for minutes.
/// </summary>
public sealed class StageAndIncrementalCacheTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("gridart-stage-").FullName;

    /// <summary>
    /// Effectively "immediately, but still enabled". These fixtures load in a few milliseconds, so any
    /// interval a human would type leaves the run finished before the first stage is due.
    /// </summary>
    private const double FastStageInterval = 0.000001;

    [Fact]
    public void Cache_entries_are_readable_before_Save_is_called()
    {
        // The whole point of appending: an entry must be on disk the moment it is decoded, not at the
        // end of a run that may never reach its end.
        var tiles = CreateTileFolder("tiles", 5);
        var cacheDir = Path.Combine(root, "append");

        var writer = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance);
        foreach (var file in Directory.GetFiles(tiles).Order())
        {
            using var image = new Image<Rgba32>(8, 8, new Rgba32(10, 20, 30));
            writer.Set(new FileInfo(file), image);
        }

        // Deliberately no Save() — a killed process never gets to call it.
        var reader = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance);
        Assert.Equal(5, reader.LoadedCount);
        Assert.Equal(5, writer.AppendedCount);
    }

    [Fact]
    public void A_torn_final_record_is_dropped_and_the_rest_survives()
    {
        // Killing a process mid-append leaves a partial record. Everything before it is still valid
        // work and must not be thrown away.
        var tiles = CreateTileFolder("tiles", 6);
        var cacheDir = Path.Combine(root, "torn");
        var cachePath = TileCache.ResolveCachePath(cacheDir, tiles, new Size(8, 8));

        var writer = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance);
        foreach (var file in Directory.GetFiles(tiles).Order())
        {
            using var image = new Image<Rgba32>(8, 8, new Rgba32(90, 90, 90));
            writer.Set(new FileInfo(file), image);
        }
        writer.Dispose();

        // Chop a record in half, then trail some junk, as an interrupted write would.
        var intact = File.ReadAllBytes(cachePath);
        var perRecord = (intact.Length - 12) / 6;
        var truncated = intact.Take(12 + perRecord * 4 + perRecord / 2).ToArray();
        File.WriteAllBytes(cachePath, truncated);

        var reader = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance);
        Assert.Equal(4, reader.LoadedCount);
    }

    [Fact]
    public void Appending_after_a_torn_record_overwrites_the_garbage()
    {
        var tiles = CreateTileFolder("tiles", 4);
        var cacheDir = Path.Combine(root, "reappend");
        var cachePath = TileCache.ResolveCachePath(cacheDir, tiles, new Size(8, 8));
        var files = Directory.GetFiles(tiles).Order().ToArray();

        var first = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance);
        using (var image = new Image<Rgba32>(8, 8, new Rgba32(1, 2, 3)))
        {
            first.Set(new FileInfo(files[0]), image);
        }
        first.Dispose();

        // Simulate the tail of an interrupted append.
        using (var stream = File.Open(cachePath, FileMode.Append))
        {
            stream.Write([0x11, 0x22, 0x33]);
        }

        var second = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance);
        Assert.Equal(1, second.LoadedCount);

        using (var image = new Image<Rgba32>(8, 8, new Rgba32(4, 5, 6)))
        {
            second.Set(new FileInfo(files[1]), image);
        }
        second.Dispose();

        // Both entries must be readable, so the junk was truncated rather than appended past.
        var third = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance);
        Assert.Equal(2, third.LoadedCount);
    }

    [Fact]
    public async Task Save_does_not_rewrite_the_file_when_every_entry_was_appended()
    {
        // The multi-megabyte end-of-run write is exactly what moving to appends was meant to remove.
        var basePath = CreateQuadrantBaseImage("base.png", 160, 160);
        var tiles = CreateTileFolder("tiles", 10);
        var cacheDir = Path.Combine(root, "nowrite");

        using (await BuildAsync(basePath, tiles, cacheDir)) { }

        var cachePath = TileCache.ResolveCachePath(cacheDir, tiles, new Size(8, 8));
        var writtenAt = File.GetLastWriteTimeUtc(cachePath);

        var cache = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance);
        Assert.False(
            cache.Save(Directory.GetFiles(tiles)),
            "Nothing changed, so the cache must not be rewritten.");
        Assert.Equal(writtenAt, File.GetLastWriteTimeUtc(cachePath));
    }

    [Fact]
    public void Save_keeps_entries_whose_live_paths_arrive_relative()
    {
        // `gridart base.png tiles` enumerates relative paths, while entries are keyed on FullName.
        // Comparing the two forms directly made every entry look deleted, so Save pruned the entire
        // cache and rewrote it empty — the cache never hit, on any run, from any relative invocation.
        var tiles = CreateTileFolder("relative", 5);
        var cacheDir = Path.Combine(root, "relative-cache");

        var writer = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance);
        foreach (var file in Directory.GetFiles(tiles).Order())
        {
            using var image = new Image<Rgba32>(8, 8, new Rgba32(50, 60, 70));
            writer.Set(new FileInfo(file), image);
        }

        // Relative to the real working directory rather than by changing it: the current directory is
        // process-global and xUnit runs test classes in parallel.
        var cwd = Directory.GetCurrentDirectory();
        var relative = Directory.GetFiles(tiles)
            .Select(file => Path.GetRelativePath(cwd, file))
            .ToArray();
        Assert.False(Path.IsPathRooted(relative[0]), "The fixture must exercise relative paths.");

        Assert.False(writer.Save(relative), "Nothing is stale, so nothing may be pruned or rewritten.");
        writer.Dispose();
        Assert.Equal(5, TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance).LoadedCount);
    }

    [Fact]
    public void Save_still_compacts_when_entries_must_be_pruned()
    {
        var tiles = CreateTileFolder("tiles", 6);
        var cacheDir = Path.Combine(root, "prune");
        var files = Directory.GetFiles(tiles).Order().ToArray();

        var writer = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance);
        foreach (var file in files)
        {
            using var image = new Image<Rgba32>(8, 8, new Rgba32(70, 70, 70));
            writer.Set(new FileInfo(file), image);
        }

        Assert.True(writer.Save(files.Take(3).ToArray()), "Pruning three entries requires a rewrite.");
        writer.Dispose();

        Assert.Equal(3, TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance).LoadedCount);
    }

    [Fact]
    public async Task Incremental_cache_still_produces_an_identical_mosaic()
    {
        // The invariant a cache must satisfy: it changes speed, never pixels.
        var basePath = CreateQuadrantBaseImage("base.png", 240, 240);
        var tiles = CreateTileFolder("tiles", 16);
        var cacheDir = Path.Combine(root, "identity");

        var cold = await BuildToBytesAsync(basePath, tiles, cacheDir);
        var warm = await BuildToBytesAsync(basePath, tiles, cacheDir);
        var uncached = await BuildToBytesAsync(basePath, tiles, cacheDir, noCache: true);

        Assert.Equal(cold, warm);
        Assert.Equal(cold, uncached);
    }

    [Fact]
    public async Task A_partial_cache_from_an_interrupted_run_is_reused()
    {
        // Half a cache is exactly what a Ctrl-C leaves behind, and it must be worth something.
        var basePath = CreateQuadrantBaseImage("base.png", 200, 200);
        var tiles = CreateTileFolder("tiles", 12);
        var cacheDir = Path.Combine(root, "partial");
        var files = Directory.GetFiles(tiles).Order().ToArray();

        var interrupted = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance);
        foreach (var file in files.Take(5))
        {
            using var image = await Image.LoadAsync<Rgba32>(file);
            image.Mutate(ctx => ctx.Resize(8, 8));
            interrupted.Set(new FileInfo(file), image);
        }
        interrupted.Dispose(); // No Save: the process "died" here.

        var resumed = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance);
        Assert.Equal(5, resumed.LoadedCount);

        // And a full run on top of it produces the same mosaic as one from an empty cache.
        var fromPartial = await BuildToBytesAsync(basePath, tiles, cacheDir);
        var fromScratch = await BuildToBytesAsync(basePath, tiles, Path.Combine(root, "partial-control"));
        Assert.Equal(fromScratch, fromPartial);
    }

    [Fact]
    public async Task Stage_images_are_written_while_tiles_load()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 200, 200);
        var tiles = CreateTileFolder("tiles", 60);
        var output = Path.Combine(root, "staged", "out.png");

        using var result = await BuildAsync(
            basePath, tiles, Path.Combine(root, "stage-cache"), output, FastStageInterval);

        var stages = StageFiles(output);
        Assert.NotEmpty(stages);

        // A stage is a preview of the real thing, so it must be viewable at the final dimensions.
        using var stage = await Image.LoadAsync<Rgba32>(stages[0]);
        Assert.Equal(result.Image.Width, stage.Width);
        Assert.Equal(result.Image.Height, stage.Height);
    }

    [Fact]
    public async Task Stage_files_are_numbered_consecutively_from_one()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 160, 160);
        var tiles = CreateTileFolder("tiles", 80);
        var output = Path.Combine(root, "numbered.png");

        using (await BuildAsync(basePath, tiles, Path.Combine(root, "num-cache"), output, FastStageInterval)) { }

        var stages = StageFiles(output);

        // Gaps would mean a claimed slot produced nothing, which reads as a lost stage.
        var expected = Enumerable.Range(1, stages.Length)
            .Select(i => Path.Combine(root, $"numbered.stage-{i:D3}.png"));
        Assert.Equal(expected, stages);
    }

    [Fact]
    public async Task Stage_files_sit_beside_the_output_and_keep_its_extension()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 160, 160);
        var tiles = CreateTileFolder("tiles", 40);
        var output = Path.Combine(root, "nested-out", "picture.jpg");

        using (await BuildAsync(basePath, tiles, Path.Combine(root, "ext-cache"), output, FastStageInterval)) { }

        var stages = Directory.GetFiles(Path.GetDirectoryName(output)!, "picture.stage-*.jpg");
        Assert.NotEmpty(stages);
    }

    [Fact]
    public async Task Stages_are_off_when_the_interval_is_zero()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 160, 160);
        var tiles = CreateTileFolder("tiles", 20);
        var output = Path.Combine(root, "nostages.png");

        using (await BuildAsync(basePath, tiles, Path.Combine(root, "off-cache"), output, stageInterval: 0)) { }

        Assert.Empty(StageFiles(output));
    }

    [Fact]
    public async Task A_run_shorter_than_the_interval_writes_no_stages()
    {
        // Stages exist for multi-minute runs; a quick one should not litter the output folder.
        var basePath = CreateQuadrantBaseImage("base.png", 120, 120);
        var tiles = CreateTileFolder("tiles", 8);
        var output = Path.Combine(root, "quick.png");

        using (await BuildAsync(basePath, tiles, Path.Combine(root, "quick-cache"), output, stageInterval: 600)) { }

        Assert.Empty(StageFiles(output));
    }

    [Fact]
    public async Task Stages_do_not_change_the_final_mosaic()
    {
        // Stages share the tile library and the cell signatures with the real build. If a preview
        // mutated either — use counts being the obvious way — the output would silently drift.
        var basePath = CreateQuadrantBaseImage("base.png", 200, 200);
        var tiles = CreateTileFolder("tiles", 48);

        var withStages = await BuildToBytesAsync(
            basePath, tiles, Path.Combine(root, "with-stages"),
            Path.Combine(root, "with", "out.png"), FastStageInterval);

        var withoutStages = await BuildToBytesAsync(
            basePath, tiles, Path.Combine(root, "without-stages"),
            Path.Combine(root, "without", "out.png"), stageInterval: 0);

        Assert.Equal(withoutStages, withStages);
    }

    [Fact]
    public async Task Stages_do_not_break_a_run_that_cannot_write_them()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 160, 160);
        var tiles = CreateTileFolder("tiles", 40);

        // A directory where the stage file should go: saving must fail, the mosaic must not.
        var output = Path.Combine(root, "blocked", "out.png");
        Directory.CreateDirectory(Path.Combine(root, "blocked", "out.stage-001.png"));

        using var result = await BuildAsync(
            basePath, tiles, Path.Combine(root, "blocked-cache"), output, FastStageInterval);

        Assert.True(result.Columns > 0);
    }

    [Fact]
    public async Task Stage_interval_is_settable_from_the_command_line()
    {
        Assert.Equal("Mosaic:StageIntervalSeconds", CommandLine.SwitchMappings["--stage-interval"]);

        var basePath = CreateQuadrantBaseImage("base.png", 160, 160);
        var tiles = CreateTileFolder("tiles", 12);
        var output = Path.Combine(root, "cli.png");

        // -d 0 -r 0: this asserts the --stage-interval mapping, and 12 tiles can neither satisfy the
        // default repeat distance on a 10x10 grid nor cover its 100 cells without reuse.
        var exitCode = await RunCliAsync(
            [basePath, tiles, "-n", "10", "-s", "8", "-o", output, "-f", "--stage-interval", "0",
             "-d", "0", "-r", "0"]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(output));
        Assert.Empty(StageFiles(output));
        await Task.CompletedTask;
    }

    [Fact]
    public void Schedule_fires_once_the_interval_has_passed_and_only_one_at_a_time()
    {
        var schedule = new StageSchedule(TimeSpan.Zero.Add(TimeSpan.FromTicks(1)));
        Assert.True(schedule.Enabled);

        Thread.Sleep(5);
        Assert.True(schedule.TryClaim(out var first));
        Assert.Equal(1, first);

        // Held claim: a second loader thread must not start rendering in parallel.
        Assert.False(schedule.TryClaim(out _));

        schedule.Release(TimeSpan.Zero, produced: true);
        Assert.Equal(1, schedule.Written);
    }

    [Fact]
    public void Schedule_does_not_consume_a_number_for_a_claim_that_produced_nothing()
    {
        var schedule = new StageSchedule(TimeSpan.FromTicks(1));

        Thread.Sleep(5);
        Assert.True(schedule.TryClaim(out var first));
        schedule.Release(TimeSpan.Zero, produced: false);

        Thread.Sleep(5);
        Assert.True(schedule.TryClaim(out var second));
        Assert.Equal(first, second);
        Assert.Equal(0, schedule.Written);
    }

    [Fact]
    public void Schedule_backs_off_by_what_the_last_stage_cost()
    {
        // A stage that takes 2s must not be re-run under a 10ms interval, or previews would eat the run.
        var schedule = new StageSchedule(TimeSpan.FromMilliseconds(10));

        Thread.Sleep(15);
        Assert.True(schedule.TryClaim(out _));
        schedule.Release(TimeSpan.FromSeconds(2), produced: true);

        Thread.Sleep(30);
        Assert.False(schedule.TryClaim(out _));
    }

    [Fact]
    public void Schedule_is_disabled_by_a_zero_interval()
    {
        var schedule = new StageSchedule(TimeSpan.Zero);

        Assert.False(schedule.Enabled);
        Assert.False(schedule.TryClaim(out _));
    }

    private string[] StageFiles(string output)
    {
        var directory = Path.GetDirectoryName(output)!;
        return Directory.Exists(directory)
            ? Directory
                .GetFiles(directory, $"{Path.GetFileNameWithoutExtension(output)}.stage-*")
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
    }

    private static async Task<MosaicResult> BuildAsync(
        string basePath,
        string tiles,
        string cacheDir,
        string? output = null,
        double stageInterval = 0,
        bool noCache = false)
    {
        var options = new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 10,
            TileSize = 8,
            OutputPath = output,
            CacheDirectory = cacheDir,
            NoCache = noCache,
            StageIntervalSeconds = stageInterval,

            // These fixtures are about caching and stages, not placement, and some use as few as 8
            // tiles — far below what an absolute repeat distance needs for a 10x10 grid. Set to 0
            // deliberately: the constraint is not relaxed for anyone, so a test that does not care
            // about it has to say so rather than quietly get a weaker version of it.
            RepeatAvoidanceRadius = 0,

            // Same reasoning for reuse: the default is "each image once", which a 10x10 grid cannot do
            // with 8 tiles. Reuse is what these fixtures need in order to exist at all, so they ask for
            // it explicitly instead of the builder quietly allowing it.
            MaxTileReuse = 0,
        };

        return await new MosaicBuilder(NullLogger<MosaicBuilder>.Instance)
            .BuildAsync(options, CancellationToken.None);
    }

    private static async Task<byte[]> BuildToBytesAsync(
        string basePath,
        string tiles,
        string cacheDir,
        string? output = null,
        double stageInterval = 0,
        bool noCache = false)
    {
        using var result = await BuildAsync(basePath, tiles, cacheDir, output, stageInterval, noCache);

        using var stream = new MemoryStream();
        await result.Image.SaveAsPngAsync(stream);
        return stream.ToArray();
    }

    private static async Task<int> RunCliAsync(string[] args)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add(typeof(MosaicOptions).Assembly.Location);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the worker process.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        await stdout;
        await stderr;

        return process.ExitCode;
    }

    private string CreateQuadrantBaseImage(string name, int width, int height)
    {
        var path = Path.Combine(root, name);
        using var image = new Image<Rgba32>(width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = (x < width / 2, y < height / 2) switch
                {
                    (true, true) => new Rgba32(220, 30, 30),
                    (false, true) => new Rgba32(30, 190, 60),
                    (true, false) => new Rgba32(40, 70, 230),
                    (false, false) => new Rgba32(240, 220, 40),
                };
            }
        }

        image.Save(path);
        return path;
    }

    private string CreateTileFolder(string name, int count)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);

        for (var i = 0; i < count; i++)
        {
            var color = new Rgba32(
                (byte)(30 + i * 3 % 220),
                (byte)(200 - i * 5 % 190),
                (byte)(60 + i * 7 % 190),
                byte.MaxValue);

            using var tile = new Image<Rgba32>(32, 32, color);
            tile.Save(Path.Combine(folder, $"tile{i:D3}.png"));
        }

        return folder;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder left behind is not worth failing a test run over.
        }
    }
}
