namespace gridart.Imaging;

/// <summary>
/// sRGB / linear-light / CIELAB conversions used for perceptual tile matching.
/// All conversions assume the sRGB transfer function and a D65 white point.
/// </summary>
public static class ColorMath
{
    // D65 reference white in XYZ, scaled so Y == 1.
    private const float WhiteX = 0.95047f;
    private const float WhiteY = 1.00000f;
    private const float WhiteZ = 1.08883f;

    private const int LinearToSrgbLutSize = 4096;

    private static readonly float[] SrgbToLinearLut = BuildSrgbToLinearLut();
    private static readonly byte[] LinearToSrgbLut = BuildLinearToSrgbLut();

    /// <summary>Expands a gamma-encoded sRGB byte to linear light in the 0..1 range.</summary>
    public static float SrgbToLinear(byte value) => SrgbToLinearLut[value];

    /// <summary>Compresses a linear-light value in the 0..1 range back to a gamma-encoded sRGB byte.</summary>
    public static byte LinearToSrgb(float linear)
    {
        var index = (int)MathF.Round(Math.Clamp(linear, 0f, 1f) * (LinearToSrgbLutSize - 1));
        return LinearToSrgbLut[index];
    }

    /// <summary>Converts linear-light RGB (0..1) to CIELAB.</summary>
    public static (float L, float A, float B) LinearRgbToLab(float r, float g, float b)
    {
        var x = 0.4124564f * r + 0.3575761f * g + 0.1804375f * b;
        var y = 0.2126729f * r + 0.7151522f * g + 0.0721750f * b;
        var z = 0.0193339f * r + 0.1191920f * g + 0.9503041f * b;

        var fx = LabF(x / WhiteX);
        var fy = LabF(y / WhiteY);
        var fz = LabF(z / WhiteZ);

        return (116f * fy - 16f, 500f * (fx - fy), 200f * (fy - fz));
    }

    /// <summary>
    /// Squared CIE76 colour difference. Squared distances are kept throughout matching so no
    /// square roots are needed in the inner loop; take <see cref="MathF.Sqrt"/> to report a ΔE.
    /// </summary>
    public static float DeltaE76Squared(
        float l1, float a1, float b1,
        float l2, float a2, float b2)
    {
        var dl = l1 - l2;
        var da = a1 - a2;
        var db = b1 - b2;
        return dl * dl + da * da + db * db;
    }

    private static float LabF(float t) =>
        t > 0.008856452f ? MathF.Cbrt(t) : 7.787037f * t + 16f / 116f;

    private static float[] BuildSrgbToLinearLut()
    {
        var lut = new float[256];
        for (var i = 0; i < lut.Length; i++)
        {
            var v = i / 255f;
            lut[i] = v <= 0.04045f ? v / 12.92f : MathF.Pow((v + 0.055f) / 1.055f, 2.4f);
        }
        return lut;
    }

    private static byte[] BuildLinearToSrgbLut()
    {
        var lut = new byte[LinearToSrgbLutSize];
        for (var i = 0; i < lut.Length; i++)
        {
            var v = i / (float)(LinearToSrgbLutSize - 1);
            var encoded = v <= 0.0031308f ? v * 12.92f : 1.055f * MathF.Pow(v, 1f / 2.4f) - 0.055f;
            lut[i] = (byte)Math.Clamp(MathF.Round(encoded * 255f), 0f, 255f);
        }
        return lut;
    }
}
