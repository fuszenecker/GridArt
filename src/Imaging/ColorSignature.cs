using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace gridart.Imaging;

/// <summary>
/// A coarse perceptual fingerprint of an image region: the region is split into a
/// <see cref="Grid"/> × <see cref="Grid"/> lattice and each patch is reduced to a single CIELAB
/// colour. Averaging happens in linear light, which is why a 50 % grey patch does not drift dark.
/// A 1×1 signature is just the average colour; larger grids also capture internal structure, so a
/// tile with a bright top and dark bottom will not be matched against a uniform cell.
/// </summary>
public sealed class ColorSignature
{
    private readonly float[] lab;

    private ColorSignature(int grid, float[] lab)
    {
        Grid = grid;
        this.lab = lab;
    }

    /// <summary>Number of patches per axis.</summary>
    public int Grid { get; }

    /// <summary>Mean linear-light red channel of the whole region (0..1).</summary>
    public float MeanLinearR { get; private init; }

    /// <summary>Mean linear-light green channel of the whole region (0..1).</summary>
    public float MeanLinearG { get; private init; }

    /// <summary>Mean linear-light blue channel of the whole region (0..1).</summary>
    public float MeanLinearB { get; private init; }

    /// <summary>
    /// Mean squared CIE76 difference between the two signatures. Both must share the same
    /// <see cref="Grid"/>. Lower is a better match.
    /// </summary>
    public float DistanceTo(ColorSignature other)
    {
        if (other.Grid != Grid)
        {
            throw new ArgumentException(
                $"Signature grid mismatch: {Grid} vs {other.Grid}.", nameof(other));
        }

        var total = 0f;
        for (var i = 0; i < lab.Length; i += 3)
        {
            total += ColorMath.DeltaE76Squared(
                lab[i], lab[i + 1], lab[i + 2],
                other.lab[i], other.lab[i + 1], other.lab[i + 2]);
        }

        return total / (lab.Length / 3);
    }

    /// <summary>Computes the signature of <paramref name="region"/> inside <paramref name="image"/>.</summary>
    public static ColorSignature Compute(Image<Rgba32> image, Rectangle region, int grid)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(grid, 1);

        var patchCount = grid * grid;
        var sumR = new double[patchCount];
        var sumG = new double[patchCount];
        var sumB = new double[patchCount];
        var counts = new long[patchCount];

        // Patch boundaries are derived from scaled integer division so the patches tile the region
        // exactly even when width/height are not multiples of the grid size.
        image.ProcessPixelRows(accessor =>
        {
            for (var y = region.Top; y < region.Bottom; y++)
            {
                var row = accessor.GetRowSpan(y);
                var patchY = (y - region.Top) * grid / region.Height;
                var rowOffset = patchY * grid;

                for (var x = region.Left; x < region.Right; x++)
                {
                    var patch = rowOffset + (x - region.Left) * grid / region.Width;
                    var pixel = row[x];
                    sumR[patch] += ColorMath.SrgbToLinear(pixel.R);
                    sumG[patch] += ColorMath.SrgbToLinear(pixel.G);
                    sumB[patch] += ColorMath.SrgbToLinear(pixel.B);
                    counts[patch]++;
                }
            }
        });

        var lab = new float[patchCount * 3];
        double totalR = 0, totalG = 0, totalB = 0;
        long totalCount = 0;

        for (var patch = 0; patch < patchCount; patch++)
        {
            var count = counts[patch];
            if (count == 0)
            {
                // Degenerate patch (region smaller than the grid); leave it at black so it still
                // contributes a consistent, comparable value on both sides of the match.
                continue;
            }

            var r = (float)(sumR[patch] / count);
            var g = (float)(sumG[patch] / count);
            var b = (float)(sumB[patch] / count);

            var (l, a, bb) = ColorMath.LinearRgbToLab(r, g, b);
            lab[patch * 3] = l;
            lab[patch * 3 + 1] = a;
            lab[patch * 3 + 2] = bb;

            totalR += sumR[patch];
            totalG += sumG[patch];
            totalB += sumB[patch];
            totalCount += count;
        }

        var scale = totalCount == 0 ? 0d : 1d / totalCount;
        return new ColorSignature(grid, lab)
        {
            MeanLinearR = (float)(totalR * scale),
            MeanLinearG = (float)(totalG * scale),
            MeanLinearB = (float)(totalB * scale),
        };
    }

    /// <summary>Computes the signature of a whole image.</summary>
    public static ColorSignature Compute(Image<Rgba32> image, int grid) =>
        Compute(image, image.Bounds, grid);
}
