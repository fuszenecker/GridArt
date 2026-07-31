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
