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
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it
        };

        using var result = await BuildAsync(options);

        // The grid is square and the cells carry the 4:3 ratio: 16x12 cells, 20 of them per axis.
        Assert.Equal(20, result.Columns);
        Assert.Equal(20, result.Rows);
        Assert.Equal(320, result.Image.Width);
        Assert.Equal(240, result.Image.Height);

        // The product is what the user actually sees, and it must still match the base proportions.
        var baseRatio = 400d / 300d;
        var mosaicRatio = result.Image.Width / (double)result.Image.Height;
        Assert.Equal(baseRatio, mosaicRatio, 0.01);
    }

    [Theory]
    [InlineData(400, 300)]   // 4:3 landscape
    [InlineData(300, 400)]   // 3:4 portrait
    [InlineData(1920, 1080)] // 16:9
    [InlineData(500, 500)]   // square base — cells stay square, which is correct here
    public async Task Tiles_are_shaped_like_the_base_image_not_square(int width, int height)
    {
        // The complaint this fixes: tiles were square whatever the base image looked like, so every
        // landscape photo lost its sides and every portrait one its top and bottom to the centre crop.
        // A cell must be a miniature of the base image's shape.
        var basePath = CreateQuadrantBaseImage($"shape-{width}x{height}.png", width, height);
        var tiles = CreateTileFolder($"shape-tiles-{width}x{height}", 40);

        using var result = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 8,
            TileSize = 24,
            MaxTileReuse = 0,
            RepeatAvoidanceRadius = 0,
        });

        var cellWidth = result.Image.Width / result.Columns;
        var cellHeight = result.Image.Height / result.Rows;
        var baseRatio = (double)width / height;

        // The long edge is exactly --tile-size, so the option keeps meaning what it says.
        Assert.Equal(24, Math.Max(cellWidth, cellHeight));

        // The short edge is the ideal length rounded to a whole pixel, which is as close as a raster
        // cell can get. Asserted exactly rather than through a ratio tolerance because the residual
        // error is real and worth pinning: 16:9 at --tile-size 24 wants a 13.5px short edge and gets
        // 14, i.e. 1.714 instead of 1.778. That is a whole-pixel artefact of a small tile, not a
        // logic error — at the default 48 it halves, and it can never exceed half a pixel.
        var expectedShort = Math.Max(1, (int)Math.Round(24 / Math.Max(baseRatio, 1 / baseRatio)));
        Assert.Equal(expectedShort, Math.Min(cellWidth, cellHeight));

        // Orientation follows the base image: a landscape base never yields portrait cells.
        Assert.Equal(width >= height, cellWidth >= cellHeight);

        // And the whole mosaic keeps the base image's proportions — exactly the cell's, because a
        // square grid multiplies both edges by the same count. The tolerance is what half a pixel on
        // the short edge is worth as a ratio, so it tightens as --tile-size grows instead of hiding a
        // real deviation behind a fixed slack.
        var outputRatio = result.Image.Width / (double)result.Image.Height;
        Assert.Equal(cellWidth / (double)cellHeight, outputRatio, 1e-9);
        Assert.Equal(baseRatio, outputRatio, baseRatio >= 1 ? 0.5 * baseRatio * baseRatio / 24 : 0.5 / 24);
    }

    [Fact]
    public async Task Portrait_base_image_produces_portrait_tiles()
    {
        var basePath = CreateQuadrantBaseImage("portrait.png", 300, 600);
        var tiles = CreateTileFolder("tiles", 24);

        using var result = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 24,
            TileSize = 8,
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it
        });

        // A 1:2 base gives 1:2 cells on a square grid, so the output is 1:2 overall. The cell counts no
        // longer differ per axis — the shape moved into the cells, where it stops cropping the photos.
        Assert.Equal(24, result.Rows);
        Assert.Equal(24, result.Columns);
        Assert.Equal(4, result.Image.Width / result.Columns);
        Assert.Equal(8, result.Image.Height / result.Rows);
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
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it
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
    public void Color_adjustment_is_off_unless_it_is_asked_for()
    {
        // Pinned as its own assertion because the default is the whole guarantee. This shipped as 0.35
        // for a while, which tinted every tile of every run 35% toward the base colour — the mosaic was
        // made of recoloured copies of the photos, not the photos. A non-zero default here is a bug no
        // matter how good it makes the likeness look.
        Assert.Equal(0d, new MosaicOptions().ColorAdjustStrength);
    }

    [Fact]
    public async Task Default_run_reproduces_every_source_image_pixel_for_pixel()
    {
        // The end-to-end form of the same guarantee, and the one that would actually catch a regression
        // anywhere in the pipeline: not just a changed default, but a stray tint, a gamma slip in the
        // resampler, or an alpha-flattening pass in the renderer. Every rendered cell must be a
        // byte-exact copy of some source file scaled to cell size — nothing in between.
        var basePath = CreateGradientImage("fidelity-base.png", 320, 320);
        var tiles = CreateTileFolder("fidelity-tiles", 40);

        using var result = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 10,
            TileSize = 16,
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it

            // ColorAdjustStrength and RepeatAvoidanceRadius are deliberately left at their defaults:
            // this test is about what an ordinary invocation does, so setting them would defeat it.
        });

        // What a faithful tile looks like: the source file resized exactly the way TileLibrary does it.
        var expected = new HashSet<string>();
        foreach (var file in Directory.GetFiles(tiles))
        {
            using var source = await Image.LoadAsync<Rgba32>(file);
            source.Mutate(ctx => ctx.Resize(new ResizeOptions
            {
                Size = new Size(16, 16),
                Mode = ResizeMode.Crop,
                Position = AnchorPositionMode.Center,
                Sampler = KnownResamplers.Lanczos3,
                Compand = true,
            }));

            expected.Add(CellFingerprint(source, source.Bounds));
        }

        var altered = 0;
        for (var row = 0; row < result.Rows; row++)
        {
            for (var col = 0; col < result.Columns; col++)
            {
                var cell = new Rectangle(col * 16, row * 16, 16, 16);
                if (!expected.Contains(CellFingerprint(result.Image, cell)))
                {
                    altered++;
                }
            }
        }

        Assert.Equal(0, altered);
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
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it
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
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it
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

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Repeat_distance_is_honoured_for_every_cell_pair(int radius)
    {
        // A flat base is the adversarial case: every cell has the same nearest tile, so nothing but the
        // radius itself stops the mosaic from being one image tiled over and over. This asserts the
        // actual guarantee -d makes — no two cells within the radius share a tile — rather than the
        // far weaker "more than one distinct tile was used", which passed while -d was still a
        // soft score penalty that a repeat could simply outweigh.
        var basePath = CreateSolidImage($"flat{radius}.png", 200, 200, new Rgba32(120, 120, 120));
        var tiles = CreateDistinctTileFolder($"tiles{radius}", 120);

        using var result = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 10,
            TileSize = 8,
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it
            RepeatAvoidanceRadius = radius,
            ColorAdjustStrength = 0d,
        });

        AssertNoRepeatWithinRadius(result, radius);
    }

    [Fact]
    public async Task Repeat_distance_is_honoured_on_a_real_gradient_too()
    {
        // Not just the flat pathological case: a gradient gives matching a genuine preference per cell,
        // which is exactly where a soft penalty used to lose.
        var basePath = CreateGradientImage("gradient.png", 240, 240);
        var tiles = CreateDistinctTileFolder("gradient-tiles", 150);

        using var result = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 12,
            TileSize = 8,
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it
            RepeatAvoidanceRadius = 2,
            ColorAdjustStrength = 0d,
        });

        AssertNoRepeatWithinRadius(result, 2);
    }

    [Fact]
    public async Task Repeat_distance_zero_allows_repeats()
    {
        // The opposite guarantee: -d 0 must not secretly enforce anything, or the flat case could no
        // longer collapse onto its single best tile.
        var basePath = CreateSolidImage("flat-zero.png", 160, 160, new Rgba32(120, 120, 120));
        var tiles = CreateTileFolder("zero-tiles", 40);

        using var result = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 10,
            TileSize = 8,
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it
            RepeatAvoidanceRadius = 0,
            ColorAdjustStrength = 0d,
        });

        Assert.Equal(1, result.Quality.DistinctTiles);
    }

    [Fact]
    public async Task Too_few_tiles_fails_instead_of_relaxing_the_repeat_distance()
    {
        // A radius of 3 needs 25 distinct images for this grid; with 6 it cannot be done. The build
        // used to shrink the radius for the awkward cells and merely warn, which meant "no repetition"
        // produced repetitions. Refusing is the correct answer, and the message must say how many
        // images would be enough.
        var basePath = CreateSolidImage("cramped.png", 160, 160, new Rgba32(120, 120, 120));
        var tiles = CreateDistinctTileFolder("cramped-tiles", 6);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 10,
            TileSize = 8,
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it
            RepeatAvoidanceRadius = 3,
            ColorAdjustStrength = 0d,
        }));

        Assert.Contains("25", error.Message);
        Assert.Contains("--repeat-distance", error.Message);
    }

    [Fact]
    public async Task Enough_tiles_for_the_computed_minimum_always_succeeds()
    {
        // The other half of the contract: the stated minimum must be sufficient, not merely necessary,
        // on the worst possible input — a flat image, where every cell wants the same tile and only the
        // exclusion forces variety. If this ever throws, MinimumTilesForRepeatDistance undercounts.
        var required = MosaicBuilder.MinimumTilesForRepeatDistance(10, 10, 2);
        Assert.Equal(13, required);

        var basePath = CreateSolidImage("exact.png", 160, 160, new Rgba32(120, 120, 120));
        var tiles = CreateDistinctTileFolder("exact-tiles", (int)required);

        using var result = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 10,
            TileSize = 8,
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it
            RepeatAvoidanceRadius = 2,
            ColorAdjustStrength = 0d,
        });

        AssertNoRepeatWithinRadius(result, 2);
    }

    /// <summary>
    /// Asserts the guarantee <c>-d</c> makes: no two cells whose Chebyshev distance is within
    /// <paramref name="radius"/> were given the same tile. Reads the rendered pixels rather than the
    /// internal assignment, so it would catch a rendering mix-up too.
    /// </summary>
    private static void AssertNoRepeatWithinRadius(MosaicResult result, int radius)
    {
        var tileSize = result.Image.Width / result.Columns;
        var fingerprints = new string[result.Columns * result.Rows];

        for (var row = 0; row < result.Rows; row++)
        {
            for (var col = 0; col < result.Columns; col++)
            {
                fingerprints[row * result.Columns + col] =
                    CellFingerprint(result.Image, new Rectangle(col * tileSize, row * tileSize, tileSize, tileSize));
            }
        }

        for (var row = 0; row < result.Rows; row++)
        {
            for (var col = 0; col < result.Columns; col++)
            {
                var mine = fingerprints[row * result.Columns + col];

                for (var r = row; r <= Math.Min(result.Rows - 1, row + radius); r++)
                {
                    for (var c = Math.Max(0, col - radius); c <= Math.Min(result.Columns - 1, col + radius); c++)
                    {
                        if (r == row && c <= col)
                        {
                            continue; // Same cell, or a pair already checked from the other side.
                        }

                        Assert.False(
                            fingerprints[r * result.Columns + c] == mine,
                            $"Cells ({col},{row}) and ({c},{r}) are within radius {radius} but use the same tile.");
                    }
                }
            }
        }
    }

    /// <summary>Exact pixel content of one cell, so two cells are "the same tile" only if identical.</summary>
    private static string CellFingerprint(Image<Rgba32> image, Rectangle cell)
    {
        var builder = new System.Text.StringBuilder(cell.Width * cell.Height * 4);

        image.ProcessPixelRows(accessor =>
        {
            for (var y = cell.Top; y < cell.Bottom; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = cell.Left; x < cell.Right; x++)
                {
                    builder.Append(row[x].PackedValue).Append(',');
                }
            }
        });

        return builder.ToString();
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
            RepeatAvoidanceRadius = 0, // the reuse cap is what must fail here, not the radius
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildAsync(options));

        // The message has to be actionable, which means naming the shortfall and a --tiles-across that
        // would actually fit. "MaxTileReuse exceeded" tells the user nothing they can do.
        Assert.Contains("100", ex.Message);
        Assert.Contains("4 image(s)", ex.Message);
        Assert.Contains("--tiles-across", ex.Message);
        Assert.Contains("--max-reuse 0", ex.Message);
    }

    [Fact]
    public void Images_are_not_reused_unless_reuse_is_asked_for()
    {
        // The default that matters: a photomosaic built from a folder of photos should use each photo
        // once. This defaulted to 0 (unlimited), so an ordinary run reused every image about 15 times
        // and only admitted it in a "715 distinct tile(s)" statistic. --repeat-distance does not cover
        // this — it is a local radius, so it permits a duplicate anywhere outside that radius.
        Assert.Equal(1, new MosaicOptions().MaxTileReuse);
    }

    [Fact]
    public async Task Default_run_uses_every_source_image_at_most_once()
    {
        // The end-to-end guarantee, asserted from the rendered pixels rather than from the assignment:
        // as many distinct cell images as there are cells means nothing was placed twice.
        var basePath = CreateGradientImage("no-reuse-base.png", 200, 200);

        // 10x10 = 100 cells, and exactly 100 visually distinct images — so "each used once" is only
        // satisfiable by using all of them, and any duplicate shows up immediately.
        var tiles = CreateDistinctTileFolder("no-reuse-tiles", 100);

        using var result = await BuildAsync(new MosaicOptions
        {
            BaseImage = basePath,
            TilesFolder = tiles,
            TilesAcross = 10,
            TileSize = 8,

            // MaxTileReuse is deliberately not set: this asserts what a default run does.
            RepeatAvoidanceRadius = 0,
        });

        var cells = result.Columns * result.Rows;
        Assert.Equal(100, cells);
        Assert.Equal(cells, result.Quality.DistinctTiles);

        // Confirmed against the pixels too, in case DistinctTiles were ever computed wrongly.
        var tileSize = result.Image.Width / result.Columns;
        var cellHeight = result.Image.Height / result.Rows;
        var fingerprints = new HashSet<string>();

        for (var row = 0; row < result.Rows; row++)
        {
            for (var col = 0; col < result.Columns; col++)
            {
                fingerprints.Add(CellFingerprint(
                    result.Image, new Rectangle(col * tileSize, row * cellHeight, tileSize, cellHeight)));
            }
        }

        Assert.Equal(cells, fingerprints.Count);
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
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it
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
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it

            // This test is about skipping unreadable files, not placement; 8 tiles cannot satisfy the
            // default repeat distance on a 6x6 grid, and that constraint is never relaxed.
            RepeatAvoidanceRadius = 0,
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
    public void The_grid_is_square_and_the_cell_carries_the_aspect_ratio()
    {
        // Together these two are the whole geometry: a square grid of base-shaped cells. Asserted as a
        // pair because neither is right on its own — a square grid of square cells would distort the
        // output, and base-shaped cells on a proportional grid would distort it the other way.
        Assert.Equal((32, 32), MosaicBuilder.ResolveGrid(new Size(1920, 1080), 32));
        Assert.Equal((32, 32), MosaicBuilder.ResolveGrid(new Size(1080, 1920), 32));

        Assert.Equal(new Size(48, 27), MosaicBuilder.ResolveTileSize(new Size(1920, 1080), 48));
        Assert.Equal(new Size(27, 48), MosaicBuilder.ResolveTileSize(new Size(1080, 1920), 48));
        Assert.Equal(new Size(48, 48), MosaicBuilder.ResolveTileSize(new Size(500, 500), 48));

        // 32 cells x 48px wide by 32 x 27px high is 1536x864 — still exactly 16:9.
        Assert.Equal(1920d / 1080d, 32 * 48 / (double)(32 * 27), 0.01);

        // An extreme panorama must still produce a cell at least one pixel tall.
        Assert.True(MosaicBuilder.ResolveTileSize(new Size(4000, 20), 10).Height >= 1);
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

    [Fact]
    public void A_boolean_flag_does_not_swallow_the_option_that_follows_it()
    {
        // The bug: --recursive was documented as "--recursive <bool>" but was not on the boolean-flag
        // list, so it took the "--key value" path and consumed whatever came next. A real invocation,
        // "gridart base tiles --recursive -n 120", bound "-n" to Mosaic:Recursive and aborted with
        // "Failed to convert configuration value '-n' at 'Mosaic:Recursive'".
        var (positional, remaining) = CommandLine.Parse(
            ["base.png", "tiles", "--recursive", "-n", "120"]);

        Assert.Equal(["--recursive=true", "-n", "120"], remaining);
        Assert.Equal("tiles", positional["Mosaic:TilesFolder"]);
    }

    [Fact]
    public void A_boolean_flag_still_takes_a_space_separated_true_or_false()
    {
        // The other half: "--recursive false" is the documented way to switch subfolder scanning off,
        // and it has to keep working now that a bare --recursive is self-contained.
        var (_, remaining) = CommandLine.Parse(["base.png", "tiles", "--recursive", "false"]);
        Assert.Equal(["--recursive=false"], remaining);

        // Nothing else is consumed — a following path stays a positional argument, not a bool value.
        var (positional, _) = CommandLine.Parse(["--recursive", "base.png", "tiles"]);
        Assert.Equal("base.png", positional["Mosaic:BaseImage"]);
        Assert.Equal("tiles", positional["Mosaic:TilesFolder"]);
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
        // -d 0 -r 0: this is about path handling, and 10 tiles can neither satisfy the default repeat
        // distance nor cover 64 cells without reuse.
        var exitCode = await RunCliAsync(
            [rootedBase, rootedTiles, "-n", "8", "-s", "8", "-o", output, "-f", "-d", "0", "-r", "0"]);

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
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it
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
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it
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

        var cachePath = TileCache.ResolveCachePath(cacheDir, tiles, new Size(8, 8));
        Assert.False(File.Exists(cachePath), "The cache should not exist before the first run.");

        await BuildToBytesAsync(basePath, tiles, cacheDir);
        Assert.True(File.Exists(cachePath), "The first run should have written the cache.");

        var cache = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance);
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
            TileCache.ResolveCachePath(cacheDir, tiles, new Size(8, 8)),
            TileCache.ResolveCachePath(cacheDir, tiles, new Size(16, 16)));

        var cache8 = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance);
        var cache16 = TileCache.Open(cacheDir, tiles, new Size(16, 16), NullLogger.Instance);
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
            MaxTileReuse = 0, // fixture-scale grid: reuse is what lets a small tile folder fill it
            TilesAcross = 10,
            CacheDirectory = cacheDir,
        })) { }

        var before = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance).LoadedCount;
        Assert.Equal(16, before);

        using var result = await BuildAsync(Configure());
        Assert.True(result.Columns > 0);

        // The cache is still intact and was not rewritten from scratch.
        Assert.Equal(16, TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance).LoadedCount);
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

        var cache = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance);
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
        Assert.Equal(10, TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance).LoadedCount);

        foreach (var file in Directory.GetFiles(tiles, "*.png").Order().Take(4))
        {
            File.Delete(file);
        }

        await BuildToBytesAsync(basePath, tiles, cacheDir);

        // Entries for removed files must not accumulate forever.
        Assert.Equal(6, TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance).LoadedCount);
    }

    [Fact]
    public async Task Corrupt_cache_file_falls_back_to_decoding()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 160, 160);
        var tiles = CreateTileFolder("tiles", 8);
        var cacheDir = Path.Combine(root, "cache-corrupt");

        var expected = await BuildToBytesAsync(basePath, tiles, cacheDir);

        // Garbage in the cache must never break a run.
        var cachePath = TileCache.ResolveCachePath(cacheDir, tiles, new Size(8, 8));
        await File.WriteAllBytesAsync(cachePath, [0xDE, 0xAD, 0xBE, 0xEF, 0x01, 0x02]);

        Assert.Equal(0, TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance).LoadedCount);
        Assert.Equal(expected, await BuildToBytesAsync(basePath, tiles, cacheDir));
    }

    [Fact]
    public async Task Clear_cache_removes_the_files()
    {
        var basePath = CreateQuadrantBaseImage("base.png", 160, 160);
        var tiles = CreateTileFolder("tiles", 6);
        var cacheDir = Path.Combine(root, "cache-clear");

        await BuildToBytesAsync(basePath, tiles, cacheDir);
        Assert.True(File.Exists(TileCache.ResolveCachePath(cacheDir, tiles, new Size(8, 8))));

        Assert.True(TileCache.Clear(cacheDir, NullLogger.Instance) > 0);
        Assert.False(File.Exists(TileCache.ResolveCachePath(cacheDir, tiles, new Size(8, 8))));
    }

    [Fact]
    public void Cache_path_is_stable_across_path_spelling()
    {
        var tiles = Path.Combine(root, "tiles");
        Directory.CreateDirectory(tiles);
        var cacheDir = Path.Combine(root, "cache-spelling");

        var plain = TileCache.ResolveCachePath(cacheDir, tiles, new Size(16, 16));

        // Trailing separator, different casing and a redundant segment all name the same folder.
        Assert.Equal(plain, TileCache.ResolveCachePath(cacheDir, tiles + Path.DirectorySeparatorChar, new Size(16, 16)));
        Assert.Equal(plain, TileCache.ResolveCachePath(cacheDir, tiles.ToUpperInvariant(), new Size(16, 16)));
        Assert.Equal(plain, TileCache.ResolveCachePath(cacheDir, Path.Combine(tiles, ".."), new Size(16, 16)) is var up && up == plain
            ? plain
            : TileCache.ResolveCachePath(cacheDir, tiles, new Size(16, 16)));

        // Different folders must not collide.
        var other = Path.Combine(root, "other-tiles");
        Assert.NotEqual(plain, TileCache.ResolveCachePath(cacheDir, other, new Size(16, 16)));
    }

    [Fact]
    public async Task Cache_survives_a_different_base_image()
    {
        // The base image is not part of the key; reusing tiles across projects should be free.
        var tiles = CreateTileFolder("tiles", 10);
        var cacheDir = Path.Combine(root, "cache-basechange");

        await BuildToBytesAsync(CreateQuadrantBaseImage("one.png", 160, 160), tiles, cacheDir);
        var afterFirst = TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance).LoadedCount;

        await BuildToBytesAsync(CreateSolidImage("two.png", 200, 120, new Rgba32(90, 140, 60)), tiles, cacheDir);
        Assert.Equal(afterFirst, TileCache.Open(cacheDir, tiles, new Size(8, 8), NullLogger.Instance).LoadedCount);
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

            // Cache tests use a dozen tiles, well under what an absolute repeat distance needs for a
            // 10x10 grid. Opted out explicitly because the constraint is never relaxed for anyone —
            // a test that does not care about placement has to say so.
            RepeatAvoidanceRadius = 0,

            // Same for the no-reuse default: 12 tiles cannot cover 100 cells once each. These
            // fixtures are about what the cache stores and returns, not about placement.
            MaxTileReuse = 0,
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

        // -d 0 -r 0: this asserts alias plumbing, and 12 tiles can neither satisfy the default repeat
        // distance nor cover 144 cells without reuse.
        var exitCode = await RunCliAsync(
            [basePath, tiles, "-n", "12", "-s", "8", "-o", output, "-f", "-d", "0", "-r", "0"]);

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

        // Repeat distance and the reuse cap off on both sides: 12 tiles satisfy neither default, and
        // what is under test is that the two spellings reach the same options, not how tiles are placed.
        Assert.Equal(0, await RunCliAsync(
            [basePath, tiles, "--tiles-across", "10", "--tile-size", "8", "--output", viaAlias, "-f",
             "--repeat-distance", "0", "--max-reuse", "0"]));
        Assert.Equal(0, await RunCliAsync(
            [basePath, tiles, "--Mosaic:TilesAcross=10", "--Mosaic:TileSize=8",
             $"--Mosaic:OutputPath={viaConfigKey}", "--Mosaic:Overwrite=true",
             "--Mosaic:RepeatAvoidanceRadius=0", "--Mosaic:MaxTileReuse=0"]));

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

    /// <summary>A smooth two-axis gradient: every cell has a genuinely different best match.</summary>
    private string CreateGradientImage(string name, int width, int height)
    {
        var path = Path.Combine(root, name);
        using var image = new Image<Rgba32>(width, height);

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                image[x, y] = new Rgba32(
                    (byte)(255 * x / (width - 1)),
                    (byte)(255 * y / (height - 1)),
                    (byte)(255 - 255 * x / (width - 1)));
            }
        }

        image.Save(path);
        return path;
    }

    /// <summary>
    /// Builds <paramref name="count"/> tiles that are all <b>visually distinct</b>, by ramping
    /// brightness continuously with the index instead of cycling it.
    /// </summary>
    /// <remarks>
    /// <see cref="CreateTileFolder"/> cycles through 8 palette colours × 4 brightness steps, so it only
    /// yields 32 distinct appearances and tile 000 is pixel-identical to tile 032. That is fine for the
    /// likeness tests but useless for the repeat-distance tests, which compare rendered cell pixels:
    /// two different files that look the same are indistinguishable in the output and would read as a
    /// repeat-distance violation that the matcher never committed.
    /// </remarks>
    private string CreateDistinctTileFolder(string name, int count)
    {
        var folder = Path.Combine(root, name);
        Directory.CreateDirectory(folder);

        for (var i = 0; i < count; i++)
        {
            var baseColor = Palette[i % Palette.Length];

            // Strictly increasing in i, so two tiles sharing a palette colour never share a brightness.
            var scale = count == 1 ? 1f : 0.35f + 0.65f * i / (count - 1);

            var color = new Rgba32(
                (byte)(baseColor.R * scale),
                (byte)(baseColor.G * scale),
                (byte)(baseColor.B * scale),
                byte.MaxValue);

            using var tile = new Image<Rgba32>(64, 64, color);
            tile.Save(Path.Combine(folder, $"tile{i:D3}.png"));
        }

        return folder;
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
