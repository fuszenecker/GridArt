using gridart;
using gridart.Imaging;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace gridart.Tests;

/// <summary>
/// End-to-end tests over synthetic inputs. Fixtures are generated rather than committed so the
/// suite has no binary assets and the colour expectations stay self-evident.
/// </summary>
public sealed class MosaicBuilderTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("gridart-tests-").FullName;

    private static readonly Rgba32[] Palette =
    [
        new(220, 30, 30),    // red
        new(30, 190, 60),    // green
        new(40, 70, 230),    // blue
        new(240, 220, 40),   // yellow
        new(20, 20, 20),     // near-black
        new(245, 245, 245),  // near-white
        new(150, 60, 200),   // purple
        new(240, 140, 30),   // orange
    ];

    [Fact]
    public async Task Mosaic_matches_the_base_image_dimensions_ratio_and_grid()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 400, 300);
        var tiles = CreateTileFolder("tiles", 32);

        var options = new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 20,
            TileSize = 16,
        };

        using var result = await BuildAsync(options);

        // 400x300 is 4:3, so 20 columns must pair with 15 rows.
        Assert.Equal(20, result.Columns);
        Assert.Equal(15, result.Rows);
        Assert.Equal(320, result.Image.Width);
        Assert.Equal(240, result.Image.Height);

        var baseRatio = 400d / 300d;
        var mosaicRatio = result.Image.Width / (double)result.Image.Height;
        Assert.Equal(baseRatio, mosaicRatio, 0.01);
    }

    [Fact]
    public async Task Portrait_base_image_lays_tiles_across_the_long_axis()
    {
        var basePath = CreateQuadrantBaseImage("portrait.png", 300, 600);
        var tiles = CreateTileFolder("tiles", 24);

        using var result = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 24,
            TileSize = 8,
        });

        Assert.Equal(24, result.Rows);
        Assert.Equal(12, result.Columns);
    }

    [Fact]
    public async Task Zoomed_out_mosaic_resembles_the_base_image()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 480, 360);
        var tiles = CreateTileFolder("tiles", 40);

        using var result = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 24,
            TileSize = 16,
            ColorAdjustStrength = 0d, // pure tile matching — no tinting to prop up the likeness
            RepeatAvoidanceRadius = 0,
        });

        // Downscaling the mosaic to a thumbnail is literally "zooming out": the tiles disappear and
        // only the reproduced base image should remain.
        using var thumb = result.Image.Clone(ctx => ctx.Resize(48, 36, KnownResamplers.Box));
        using var baseImage = await Image.LoadAsync<Rgba32>(basePath);
        using var baseThumb = baseImage.Clone(ctx => ctx.Resize(48, 36, KnownResamplers.Box));

        var meanDeltaE = MeanPixelDeltaE(thumb, baseThumb);

        // The palette only has 8 colours, so an exact match is impossible; ΔE 25 still means each
        // quadrant is unmistakably the right colour family.
        Assert.True(meanDeltaE < 25d, $"Zoomed-out mean ΔE was {meanDeltaE:0.00}, expected < 25.");
        Assert.True(result.Quality.MeanDeltaE < 30d, $"Reported cell mean ΔE was {result.Quality.MeanDeltaE:0.00}.");
    }

    [Fact]
    public async Task Color_adjust_at_full_strength_reproduces_the_base_image()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 320, 320);
        var tiles = CreateTileFolder("tiles", 16);

        using var result = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 16,
            TileSize = 12,
            ColorAdjustStrength = 1d,
        });

        using var baseImage = await Image.LoadAsync<Rgba32>(basePath);
        using var resized = baseImage.Clone(ctx => ctx.Resize(new ResizeOptions
        {
            Size = result.Image.Size,
            Mode = ResizeMode.Stretch,
            Sampler = KnownResamplers.Lanczos3,
        }));

        // Strength 1 keeps none of the tile, so only sRGB round-trip rounding should differ.
        Assert.True(MeanPixelDeltaE(result.Image, resized) < 1.5d);
    }

    [Fact]
    public async Task Zoomed_in_mosaic_still_contains_the_source_pictures()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 320, 320);
        var tiles = CreateTileFolder("tiles", 24);

        using var result = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 16,
            TileSize = 24,
            ColorAdjustStrength = 0.35d,
        });

        // Every generated tile carries a distinctive bright diagonal stripe. If tiles survived into
        // the output, cells must have visible internal contrast rather than being flat colour.
        var flatCells = 0;
        var cells = 0;

        for (var row = 0; row < result.Rows; row++)
        {
            for (var col = 0; col < result.Columns; col++)
            {
                cells++;
                if (LuminanceRange(result.Image, new Rectangle(col * 24, row * 24, 24, 24)) < 0.04f)
                {
                    flatCells++;
                }
            }
        }

        Assert.True(flatCells < cells * 0.1, $"{flatCells} of {cells} cells were flat — tile detail was lost.");
    }

    [Fact]
    public async Task Repeat_avoidance_prevents_adjacent_duplicates()
    {
        // A single flat-colour base would otherwise pick the same nearest tile for every cell.
        var basePath = CreateSolidImage("flat.png", 200, 200, new Rgba32(120, 120, 120));
        var tiles = CreateTileFolder("tiles", 40);

        using var result = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 10,
            TileSize = 10,
            RepeatAvoidanceRadius = 1,
            ColorAdjustStrength = 0d,
        });

        Assert.True(result.Quality.DistinctTiles > 1,
            "Repeat avoidance should have forced more than one distinct tile on a flat base.");
    }

    [Fact]
    public async Task Max_tile_reuse_is_respected_and_reported_when_impossible()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 200, 200);
        var tiles = CreateTileFolder("few", 4);

        var options = new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 10, // 100 cells against 4 tiles x 1 use
            TileSize = 8,
            MaxTileReuse = 1,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(options));
        Assert.Contains("MaxTileReuse", ex.Message);
    }

    [Fact]
    public async Task Empty_tiles_folder_fails_with_a_clear_message()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 100, 100);
        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = empty,
            TilesAcross = 4,
            TileSize = 8,
        }));

        Assert.Contains("No supported images", ex.Message);
    }

    [Fact]
    public async Task Non_image_files_are_skipped_rather_than_failing_the_run()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 120, 120);
        var tiles = CreateTileFolder("tiles", 8);
        await File.WriteAllTextAsync(Path.Combine(tiles, "notes.txt"), "ignored");
        await File.WriteAllTextAsync(Path.Combine(tiles, "corrupt.png"), "this is not a png");

        using var result = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 6,
            TileSize = 8,
        });

        Assert.Equal(6, result.Columns);
    }

    [Fact]
    public void ResolveOutputPath_defaults_next_to_the_base_image()
    {
        var options = new MosaicOptions { BaseImage = Path.Combine(root, "holiday.jpg"), TilesFolder = root };
        Assert.Equal(Path.Combine(root, "holiday.mosaic.png"), options.ResolveOutputPath());
    }

    [Fact]
    public void ResolveGrid_keeps_the_aspect_ratio_and_never_returns_zero()
    {
        Assert.Equal((32, 18), MosaicBuilder.ResolveGrid(new Size(1920, 1080), 32));
        Assert.Equal((18, 32), MosaicBuilder.ResolveGrid(new Size(1080, 1920), 32));
        Assert.Equal((10, 10), MosaicBuilder.ResolveGrid(new Size(500, 500), 10));

        // An extreme panorama must still produce at least one row.
        var (_, rows) = MosaicBuilder.ResolveGrid(new Size(4000, 20), 10);
        Assert.True(rows >= 1);
    }

    [Fact]
    public void Positional_arguments_map_onto_configuration_keys()
    {
        var (positional, remaining) = CommandLine.Parse(
            ["base.png", "tiles", "--Mosaic:TilesAcross=80"]);

        Assert.Equal("base.png", positional["Mosaic:BaseImage"]);
        Assert.Equal("tiles", positional["Mosaic:TilesFolder"]);
        Assert.Equal(["--Mosaic:TilesAcross=80"], remaining);
    }

    [Fact]
    public void Space_separated_option_values_are_not_taken_as_positional_arguments()
    {
        // The regression this guards: "out.png" must not become the base image.
        var (positional, remaining) = CommandLine.Parse(
            ["-o", "out.png", "base.png", "tiles"]);

        Assert.Equal("base.png", positional["Mosaic:BaseImage"]);
        Assert.Equal("tiles", positional["Mosaic:TilesFolder"]);
        Assert.Equal(["-o", "out.png"], remaining);
    }

    [Fact]
    public void Bare_boolean_flags_are_expanded_to_an_explicit_value()
    {
        var (_, remaining) = CommandLine.Parse(["base.png", "tiles", "-f"]);
        Assert.Equal(["-f=true"], remaining);

        // An explicit value must survive untouched.
        var (_, explicitValue) = CommandLine.Parse(["base.png", "tiles", "--overwrite=false"]);
        Assert.Equal(["--overwrite=false"], explicitValue);
    }

    [Theory]
    [InlineData("-n", "Mosaic:TilesAcross")]
    [InlineData("--tiles-across", "Mosaic:TilesAcross")]
    [InlineData("-s", "Mosaic:TileSize")]
    [InlineData("-o", "Mosaic:OutputPath")]
    [InlineData("-c", "Mosaic:ColorAdjustStrength")]
    [InlineData("-d", "Mosaic:RepeatAvoidanceRadius")]
    [InlineData("--recursive", "Mosaic:Recursive")]
    [InlineData("-f", "Mosaic:Overwrite")]
    public void Aliases_target_the_expected_configuration_keys(string alias, string expectedKey)
    {
        Assert.Equal(expectedKey, CommandLine.SwitchMappings[alias]);
    }

    [Fact]
    public void Every_option_has_a_long_alias()
    {
        // Guards against a property being added to MosaicOptions without an alias entry.
        var optionNames = typeof(MosaicOptions)
            .GetProperties()
            .Select(p => p.Name)
            .Except([nameof(MosaicOptions.BaseImage), nameof(MosaicOptions.TilesFolder)])
            .ToArray();

        var mapped = CommandLine.SwitchMappings.Values
            .Select(v => v.Split(':')[1])
            .ToHashSet();

        Assert.DoesNotContain(optionNames, n => !mapped.Contains(n));
    }

    [Theory]
    [InlineData("-Z", "9")]      // silently ignored by the provider, so checked explicitly
    [InlineData("-Z=9", null)]   // the provider would throw on this one
    [InlineData("--nope", "1")]
    public void Unknown_switches_are_detected(string flag, string? value)
    {
        string[] args = value is null
            ? ["base.png", "tiles", flag]
            : ["base.png", "tiles", flag, value];

        Assert.Equal(flag.Split('=')[0], CommandLine.FindUnknownSwitch(args));
    }

    [Theory]
    [InlineData("/tmp/nested/base.png")]      // MSYS / Git Bash style
    [InlineData("/c/photos/base.png")]
    [InlineData("/mnt/d/pictures/base.png")]
    [InlineData("/base.png")]
    public void Slash_prefixed_paths_are_treated_as_paths_not_options(string path)
    {
        // Regression: these were read as Windows "/switch" options, so a valid path was rejected as
        // an unknown option and the worker printed its usage instead of building anything.
        Assert.Null(CommandLine.FindUnknownSwitch([path, "/tmp/nested/tiles"]));

        var (positional, remaining) = CommandLine.Parse([path, "/tmp/nested/tiles"]);
        Assert.Equal(path, positional["Mosaic:BaseImage"]);
        Assert.Equal("/tmp/nested/tiles", positional["Mosaic:TilesFolder"]);
        Assert.Empty(remaining);
    }

    [Fact]
    public void Windows_slash_switches_still_work()
    {
        // Dropping '/' handling entirely would have been the lazy fix; these must keep working.
        Assert.Null(CommandLine.FindUnknownSwitch(["base.png", "tiles", "/Mosaic:TilesAcross=20"]));

        var (_, remaining) = CommandLine.Parse(["base.png", "tiles", "/Mosaic:TilesAcross=20"]);
        Assert.Contains("/Mosaic:TilesAcross=20", remaining);
    }

    [Fact]
    public async Task Slash_prefixed_path_actually_builds_a_mosaic()
    {
        // End-to-end proof, using a real path that starts with a separator.
        var basePath = CreateQuadrantBaseImage("base.png", 200, 200);
        var tiles = CreateTileFolder("tiles", 10);

        // Strip the drive letter to get a rooted, slash-leading path to the same file.
        var rootedBase = '/' + Path.GetRelativePath(Path.GetPathRoot(basePath)!, basePath).Replace('\\', '/');
        var rootedTiles = '/' + Path.GetRelativePath(Path.GetPathRoot(tiles)!, tiles).Replace('\\', '/');

        Assert.Null(CommandLine.FindUnknownSwitch([rootedBase, rootedTiles]));

        var output = Path.Combine(root, "rooted.png");
        var exitCode = await RunCliAsync([rootedBase, rootedTiles, "-n", "8", "-s", "8", "-o", output, "-f"]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(output), "A slash-prefixed path should have produced a mosaic.");
    }

    [Fact]
    public async Task Tiles_in_nested_subfolders_are_all_found()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 160, 160);
        var nested = Path.Combine(root, "nested");

        // 6 tiles spread three levels deep.
        foreach (var (sub, count) in new[] { ("a", 2), (Path.Combine("a", "deep"), 2), ("b", 2) })
        {
            var folder = Path.Combine(nested, sub);
            Directory.CreateDirectory(folder);
            for (var i = 0; i < count; i++)
            {
                using var tile = new Image<Rgba32>(32, 32, Palette[(i + sub.Length) % Palette.Length]);
                tile.Save(Path.Combine(folder, $"t{i}.png"));
            }
        }

        using var recursive = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = nested,
            TilesAcross = 8,
            TileSize = 8,
            RepeatAvoidanceRadius = 0,
        });
        Assert.True(recursive.Quality.DistinctTiles > 2, "Recursive scan should reach nested folders.");

        // With Recursive off, the top-level folder holds no images at all, which must be a clear error.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = nested,
            TilesAcross = 8,
            TileSize = 8,
            Recursive = false,
        }));
        Assert.Contains("No supported images", ex.Message);
    }

    [Fact]
    public async Task Missing_argument_errors_name_the_argument_and_echo_what_was_received()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 100, 100);

        var (exit, output) = await RunCliCapturingErrorAsync([basePath]);

        Assert.Equal(1, exit);
        Assert.Contains("Missing the tiles folder", output);
        // Echoing the arguments back is what makes a swallowed path diagnosable.
        Assert.Contains(basePath, output);
    }

    [Fact]
    public async Task Cache_does_not_change_the_output()
    {
        // The only property that really matters: a cached run must be byte-identical to a cold one.
        var basePath = CreateQuadrantBaseImage("base.png", 240, 240);
        var tiles = CreateTileFolder("tiles", 16);
        var cacheDir = Path.Combine(root, "cache-identity");

        var cold = await BuildToBytesAsync(basePath, tiles, cacheDir);
        var warm = await BuildToBytesAsync(basePath, tiles, cacheDir);

        Assert.Equal(cold, warm);

        // ...and identical to a run with the cache disabled entirely.
        var uncached = await BuildToBytesAsync(basePath, tiles, cacheDir, noCache: true);
        Assert.Equal(cold, uncached);
    }

    [Fact]
    public async Task Second_run_hits_the_cache()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 160, 160);
        var tiles = CreateTileFolder("tiles", 12);
        var cacheDir = Path.Combine(root, "cache-hits");

        var cachePath = TileCache.ResolveCachePath(cacheDir, tiles, 8);
        Assert.False(File.Exists(cachePath), "The cache should not exist before the first run.");

        await BuildToBytesAsync(basePath, tiles, cacheDir);
        Assert.True(File.Exists(cachePath), "The first run should have written the cache.");

        var cache = TileCache.Open(cacheDir, tiles, 8, NullLogger.Instance);
        Assert.Equal(12, cache.LoadedCount);

        // Every entry should be a usable hit on the unchanged files.
        var hits = Directory.GetFiles(tiles, "*.png")
            .Count(f => cache.TryGet(new FileInfo(f)) is not null);
        Assert.Equal(12, hits);
    }

    [Fact]
    public async Task Different_tile_sizes_use_separate_caches()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 160, 160);
        var tiles = CreateTileFolder("tiles", 8);
        var cacheDir = Path.Combine(root, "cache-sizes");

        await BuildToBytesAsync(basePath, tiles, cacheDir, tileSize: 8);
        await BuildToBytesAsync(basePath, tiles, cacheDir, tileSize: 16);

        // Distinct files, so a 16px run can never be served 8px pixels.
        Assert.NotEqual(
            TileCache.ResolveCachePath(cacheDir, tiles, 8),
            TileCache.ResolveCachePath(cacheDir, tiles, 16));

        var cache8 = TileCache.Open(cacheDir, tiles, 8, NullLogger.Instance);
        var cache16 = TileCache.Open(cacheDir, tiles, 16, NullLogger.Instance);
        Assert.Equal(8, cache8.LoadedCount);
        Assert.Equal(8, cache16.LoadedCount);
    }

    [Theory]
    [InlineData(nameof(MosaicOptions.TilesAcross))]
    [InlineData(nameof(MosaicOptions.SignatureGrid))]
    [InlineData(nameof(MosaicOptions.ColorAdjustStrength))]
    [InlineData(nameof(MosaicOptions.MaxTileReuse))]
    [InlineData(nameof(MosaicOptions.RepeatAvoidanceRadius))]
    public async Task Options_unrelated_to_tile_pixels_still_hit_the_cache(string changedOption)
    {
        // These affect selection or compositing, not the decoded tile, so they must not invalidate
        // the cache — otherwise every parameter tweak would pay full decode cost again.
        var basePath = CreateQuadrantBaseImage("base.png", 200, 200);
        var tiles = CreateTileFolder("tiles", 16);
        var cacheDir = Path.Combine(root, $"cache-{changedOption}");

        MosaicOptions Configure() => new()
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TileSize = 8,
            TilesAcross = changedOption == nameof(MosaicOptions.TilesAcross) ? 12 : 10,
            SignatureGrid = changedOption == nameof(MosaicOptions.SignatureGrid) ? 2 : 3,
            ColorAdjustStrength = changedOption == nameof(MosaicOptions.ColorAdjustStrength) ? 0.9 : 0.35,
            MaxTileReuse = changedOption == nameof(MosaicOptions.MaxTileReuse) ? 40 : 0,
            RepeatAvoidanceRadius = changedOption == nameof(MosaicOptions.RepeatAvoidanceRadius) ? 0 : 2,
            CacheDirectory = cacheDir,
        };

        // Warm the cache with defaults, then run with the option changed.
        using (await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TileSize = 8,
            TilesAcross = 10,
            CacheDirectory = cacheDir,
        })) { }

        var before = TileCache.Open(cacheDir, tiles, 8, NullLogger.Instance).LoadedCount;
        Assert.Equal(16, before);

        using var result = await BuildAsync(Configure());
        Assert.True(result.Columns > 0);

        // The cache is still intact and was not rewritten from scratch.
        Assert.Equal(16, TileCache.Open(cacheDir, tiles, 8, NullLogger.Instance).LoadedCount);
    }

    [Fact]
    public async Task Modified_tile_file_invalidates_its_entry()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 160, 160);
        var tiles = CreateTileFolder("tiles", 6);
        var cacheDir = Path.Combine(root, "cache-invalidate");

        await BuildToBytesAsync(basePath, tiles, cacheDir);

        // Rewrite one tile with different content, size and timestamp.
        var victim = Directory.GetFiles(tiles, "*.png").Order().First();
        using (var replacement = new Image<Rgba32>(96, 96, new Rgba32(7, 250, 190)))
        {
            replacement.Save(victim);
        }
        File.SetLastWriteTimeUtc(victim, DateTime.UtcNow.AddSeconds(5));

        var cache = TileCache.Open(cacheDir, tiles, 8, NullLogger.Instance);
        Assert.Null(cache.TryGet(new FileInfo(victim)));

        // The other five are untouched and still valid.
        var stillValid = Directory.GetFiles(tiles, "*.png")
            .Where(f => f != victim)
            .Count(f => cache.TryGet(new FileInfo(f)) is not null);
        Assert.Equal(5, stillValid);
    }

    [Fact]
    public async Task Deleted_tiles_are_pruned_from_the_cache()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 160, 160);
        var tiles = CreateTileFolder("tiles", 10);
        var cacheDir = Path.Combine(root, "cache-prune");

        await BuildToBytesAsync(basePath, tiles, cacheDir);
        Assert.Equal(10, TileCache.Open(cacheDir, tiles, 8, NullLogger.Instance).LoadedCount);

        foreach (var file in Directory.GetFiles(tiles, "*.png").Order().Take(4))
        {
            File.Delete(file);
        }

        await BuildToBytesAsync(basePath, tiles, cacheDir);

        // Entries for removed files must not accumulate forever.
        Assert.Equal(6, TileCache.Open(cacheDir, tiles, 8, NullLogger.Instance).LoadedCount);
    }

    [Fact]
    public async Task Corrupt_cache_file_falls_back_to_decoding()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 160, 160);
        var tiles = CreateTileFolder("tiles", 8);
        var cacheDir = Path.Combine(root, "cache-corrupt");

        var expected = await BuildToBytesAsync(basePath, tiles, cacheDir);

        // Garbage in the cache must never break a run.
        var cachePath = TileCache.ResolveCachePath(cacheDir, tiles, 8);
        await File.WriteAllBytesAsync(cachePath, [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02]);

        Assert.Equal(0, TileCache.Open(cacheDir, tiles, 8, NullLogger.Instance).LoadedCount);
        Assert.Equal(expected, await BuildToBytesAsync(basePath, tiles, cacheDir));
    }

    [Fact]
    public async Task Clear_cache_removes_the_files()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 160, 160);
        var tiles = CreateTileFolder("tiles", 6);
        var cacheDir = Path.Combine(root, "cache-clear");

        await BuildToBytesAsync(basePath, tiles, cacheDir);
        Assert.True(File.Exists(TileCache.ResolveCachePath(cacheDir, tiles, 8)));

        Assert.True(TileCache.Clear(cacheDir, NullLogger.Instance) > 0);
        Assert.False(File.Exists(TileCache.ResolveCachePath(cacheDir, tiles, 8)));
    }

    [Fact]
    public void Cache_path_is_stable_across_path_spelling()
    {
        var tiles = Path.Combine(root, "tiles");
        Directory.CreateDirectory(tiles);
        var cacheDir = Path.Combine(root, "cache-spelling");

        var plain = TileCache.ResolveCachePath(cacheDir, tiles, 16);

        // Trailing separator, different casing and a redundant segment all name the same folder.
        Assert.Equal(plain, TileCache.ResolveCachePath(cacheDir, tiles + Path.DirectorySeparatorChar, 16));
        Assert.Equal(plain, TileCache.ResolveCachePath(cacheDir, tiles.ToUpperInvariant(), 16));
        Assert.Equal(plain, TileCache.ResolveCachePath(cacheDir, Path.Combine(tiles, ".."), 16) is var up && up == plain
            ? plain
            : TileCache.ResolveCachePath(cacheDir, tiles, 16));

        // Different folders must not collide.
        var other = Path.Combine(root, "other-tiles");
        Assert.NotEqual(plain, TileCache.ResolveCachePath(cacheDir, other, 16));
    }

    [Fact]
    public async Task Cache_survives_a_different_base_image()
    {
        // The base image is not part of the key; reusing tiles across projects should be free.
        var tiles = CreateTileFolder("tiles", 10);
        var cacheDir = Path.Combine(root, "cache-basechange");

        await BuildToBytesAsync(CreateQuadrantBaseImage("one.png", 160, 160), tiles, cacheDir);
        var afterFirst = TileCache.Open(cacheDir, tiles, 8, NullLogger.Instance).LoadedCount;

        await BuildToBytesAsync(CreateSolidImage("two.png", 200, 120, new Rgba32(90, 140, 60)), tiles, cacheDir);
        Assert.Equal(afterFirst, TileCache.Open(cacheDir, tiles, 8, NullLogger.Instance).LoadedCount);
    }

    private async Task<byte[]> BuildToBytesAsync(
        string basePath, string tiles, string cacheDir, bool noCache = false, int tileSize = 8)
    {
        using var result = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 10,
            TileSize = tileSize,
            CacheDirectory = cacheDir,
            NoCache = noCache,
        });

        using var stream = new MemoryStream();
        await result.Image.SaveAsPngAsync(stream);
        return stream.ToArray();
    }

    [Fact]
    public void Known_switches_and_configuration_keys_pass_validation()
    {
        Assert.Null(CommandLine.FindUnknownSwitch(
            ["base.png", "tiles", "-n", "40", "--tile-size=16", "-f",
             "--Mosaic:ColorAdjustStrength=0.5", "--help"]));
    }

    [Fact]
    public async Task Short_aliases_drive_a_real_run()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 240, 240);
        var tiles = CreateTileFolder("tiles", 12);
        var output = Path.Combine(root, "aliased.png");

        var exitCode = await RunCliAsync([basePath, tiles, "-n", "12", "-s", "8", "-o", output, "-f"]);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(output), "The -o alias should have produced this file.");

        using var image = await Image.LoadAsync<Rgba32>(output);
        Assert.Equal(96, image.Width);  // -n 12 x -s 8
        Assert.Equal(96, image.Height);
    }

    [Fact]
    public async Task Long_and_configuration_style_options_are_equivalent()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 240, 240);
        var tiles = CreateTileFolder("tiles", 12);
        var viaAlias = Path.Combine(root, "alias.png");
        var viaConfigKey = Path.Combine(root, "config.png");

        Assert.Equal(0, await RunCliAsync(
            [basePath, tiles, "--tiles-across", "10", "--tile-size", "8", "--output", viaAlias, "-f"]));
        Assert.Equal(0, await RunCliAsync(
            [basePath, tiles, "--Mosaic:TilesAcross=10", "--Mosaic:TileSize=8",
             $"--Mosaic:OutputPath={viaConfigKey}", "--Mosaic:Overwrite=true"]));

        Assert.Equal(await File.ReadAllBytesAsync(viaAlias), await File.ReadAllBytesAsync(viaConfigKey));
    }

    [Fact]
    public async Task Unknown_single_dash_switch_reports_usage_instead_of_crashing()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 120, 120);
        var tiles = CreateTileFolder("tiles", 8);

        var (exitCode, stderr) = await RunCliCapturingErrorAsync([basePath, tiles, "-Z", "9"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage: gridart", stderr);
    }

    [Fact]
    public async Task Missing_arguments_and_help_are_distinguished()
    {
        var (noArgsExit, noArgsErr) = await RunCliCapturingErrorAsync([]);
        Assert.Equal(1, noArgsExit);
        Assert.Contains("Usage: gridart", noArgsErr);

        Assert.Equal(0, await RunCliAsync(["--help"]));
    }

    /// <summary>
    /// Runs the built worker as a real process, which is the only way to exercise Program.cs —
    /// configuration wiring and alias handling live there, not in an injectable service.
    /// </summary>
    private static async Task<int> RunCliAsync(string[] args) =>
        (await RunCliCapturingErrorAsync(args)).ExitCode;

    private static async Task<(int ExitCode, string StandardError)> RunCliCapturingErrorAsync(string[] args)
    {
        var assemblyPath = typeof(MosaicOptions).Assembly.Location;

        var startInfo = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        startInfo.ArgumentList.Add(assemblyPath);
        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the worker process.");

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        // Usage goes to stderr, but --help goes to stdout; searching both keeps the helpers simple.
        return (process.ExitCode, await stderr + await stdout);
    }

    [Fact]
    public void Signature_distance_is_zero_for_identical_regions_and_grows_with_difference()
    {
        using var red = new Image<Rgba32>(20, 20, new Rgba32(200, 40, 40));
        using var alsoRed = new Image<Rgba32>(20, 20, new Rgba32(200, 40, 40));
        using var blue = new Image<Rgba32>(20, 20, new Rgba32(40, 40, 200));

        var a = ColorSignature.Compute(red, 2);
        var b = ColorSignature.Compute(alsoRed, 2);
        var c = ColorSignature.Compute(blue, 2);

        Assert.Equal(0f, a.DistanceTo(b), 0.0001f);
        Assert.True(a.DistanceTo(c) > 100f);
    }

    [Fact]
    public void Signature_averaging_happens_in_linear_light()
    {
        // Half black, half white. A naive sRGB average gives ~128 (L* ~54); the correct linear-light
        // average is 50 % luminance, which is L* ~76 / sRGB ~188.
        using var image = new Image<Rgba32>(2, 1);
        image[0, 0] = new Rgba32(0, 0, 0);
        image[1, 0] = new Rgba32(255, 255, 255);

        var signature = ColorSignature.Compute(image, 1);

        Assert.Equal(0.5f, signature.MeanLinearG, 0.001f);
        Assert.InRange(ColorMath.LinearToSrgb(signature.MeanLinearG), 187, 189);
    }

    [Fact]
    public void Srgb_linear_round_trip_is_stable()
    {
        for (var value = 0; value <= 255; value++)
        {
            var roundTripped = ColorMath.LinearToSrgb(ColorMath.SrgbToLinear((byte)value));
            Assert.True(Math.Abs(roundTripped - value) <= 1, $"{value} round-tripped to {roundTripped}.");
        }
    }

    private static async Task<MosaicResult> BuildAsync(MosaicOptions options) =>
        await new MosaicBuilder(NullLogger<MosaicBuilder>.Instance).BuildAsync(options, CancellationToken.None);

    /// <summary>Four saturated quadrants — an easy but unambiguous likeness target.</summary>
    private string CreateQuadrantBaseImage(string name, int width, int height)
    {
        var path = Path.Combine(root, name);
        using var image = new Image<Rgba32>(width, height);

        var halfW = width / 2;
        var halfH = height / 2;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = (x < halfW, y < halfH) switch
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

    private string CreateSolidImage(string name, int width, int height, Rgba32 color)
    {
        var path = Path.Combine(root, name);
        using var image = new Image<Rgba32>(width, height, color);
        image.Save(path);
        return path;
    }

    /// <summary>
    /// Builds <paramref name="count"/> tiles spanning the palette at several brightness levels, each
    /// with a bright diagonal stripe so "is tile detail still there?" is measurable.
    /// </summary>
    private string CreateTileFolder(string name, int count)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);

        for (var i = 0; i < count; i++)
        {
            var baseColor = Palette[i % Palette.Length];
            // Cycle brightness so nearest-colour matching has intermediate shades to choose from.
            var scale = 0.45f + 0.55f * ((i / Palette.Length) % 4) / 3f;

            var color = new Rgba32(
                (byte)(baseColor.R * scale),
                (byte)(baseColor.G * scale),
                (byte)(baseColor.B * scale),
                byte.MaxValue);

            using var tile = new Image<Rgba32>(64, 64, color);
            for (var y = 0; y < 64; y++)
            {
                for (var x = 0; x < 64; x++)
                {
                    if (Math.Abs(x - y) < 5)
                    {
                        tile[x, y] = new Rgba32(
                            (byte)Math.Min(255, color.R + 90),
                            (byte)Math.Min(255, color.G + 90),
                            (byte)Math.Min(255, color.B + 90),
                            byte.MaxValue);
                    }
                }
            }

            tile.Save(Path.Combine(folder, $"tile{i:D3}.png"));
        }

        return folder;
    }

    private static double MeanPixelDeltaE(Image<Rgba32> a, Image<Rgba32> b)
    {
        Assert.Equal(a.Size, b.Size);

        var total = 0d;
        for (var y = 0; y < a.Height; y++)
        {
            for (var x = 0; x < a.Width; x++)
            {
                var pa = a[x, y];
                var pb = b[x, y];

                var (l1, a1, b1) = ColorMath.LinearRgbToLab(
                    ColorMath.SrgbToLinear(pa.R), ColorMath.SrgbToLinear(pa.G), ColorMath.SrgbToLinear(pa.B));
                var (l2, a2, b2) = ColorMath.LinearRgbToLab(
                    ColorMath.SrgbToLinear(pb.R), ColorMath.SrgbToLinear(pb.G), ColorMath.SrgbToLinear(pb.B));

                total += Math.Sqrt(ColorMath.DeltaE76Squared(l1, a1, b1, l2, a2, b2));
            }
        }

        return total / (a.Width * (double)a.Height);
    }

    private static float LuminanceRange(Image<Rgba32> image, Rectangle region)
    {
        var min = float.MaxValue;
        var max = float.MinValue;

        for (var y = region.Top; y < region.Bottom; y++)
        {
            for (var x = region.Left; x < region.Right; x++)
            {
                var p = image[x, y];
                var luminance =
                    0.2126f * ColorMath.SrgbToLinear(p.R) +
                    0.7152f * ColorMath.SrgbToLinear(p.G) +
                    0.0722f * ColorMath.SrgbToLinear(p.B);

                min = MathF.Min(min, luminance);
                max = MathF.Max(max, luminance);
            }
        }

        return max - min;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort; a locked file must not fail the suite.
        }
    }
}
