using System.Numerics;

namespace Rendition.Passes;

/// <summary>Turns the game's <see cref="PostSettings"/> into what the tone-mapping pass reads.</summary>
internal static class PostGrade
{
    /// <summary>How much of the bloom every picture mixes in, before <see cref="PostSettings.Bloom"/> adds to it.</summary>
    public const float DefaultBloom = 0.05f;

    // The white that Temperature 0 stands for, and the ends -1 and 1 reach, in kelvin.
    private const double NeutralKelvin = 6500.0;
    private const double CoolestKelvin = 4000.0;
    private const double BluestKelvin = 12000.0;

    // How far Tint 1 moves the white off the line of black-body colours, in the CIE 1960 uv plane.
    private const double MaxDuv = 0.02;

    /// <summary>
    /// <paramref name="post"/> with what <paramref name="off"/> leaves out set back to neutral: the vignette, the grain,
    /// the sharpening, and with <see cref="RenderEffects.ColorGrading"/> the contrast, saturation, lift, gain and white
    /// balance. The exposure, the bloom, the tonemapper and the lens are left alone.
    /// </summary>
    public static PostSettings Without(PostSettings post, RenderEffects off)
    {
        if ((off & RenderEffects.Vignette) != 0)
        {
            post.Vignette = 0f;
        }

        if ((off & RenderEffects.Grain) != 0)
        {
            post.Grain = 0f;
        }

        if ((off & RenderEffects.Sharpening) != 0)
        {
            post.Sharpen = 0f;
        }

        if ((off & RenderEffects.ColorGrading) != 0)
        {
            post.Contrast = 0f;
            post.Saturation = 0f;
            post.Lift = Vector3.Zero;
            post.Gain = Vector3.Zero;
            post.Temperature = 0f;
            post.Tint = 0f;
        }

        return post;
    }

    /// <summary>The lift, with the bloom's strength in w, and the gain as a multiplier for each channel.</summary>
    public static (Vector4 Lift, Vector4 Gain) Tint(in PostSettings post)
    {
        float bloom = Math.Clamp(DefaultBloom + post.Bloom, 0f, 1f);
        var gain = Vector3.Max(Vector3.One + post.Gain, Vector3.Zero);
        return (new Vector4(post.Lift, bloom), new Vector4(gain, 0f));
    }

    /// <summary>
    /// Which curve the tone-mapping pass uses, as <c>frame.curve</c>: x is 1 for ACES and 0 for AgX;
    /// y and z are the AgX look's power and saturation, applied to the curve's display values.
    /// Blender's Punchy look is a power of 1.35 and a saturation of 1.4; the base look is 1 and 1.
    /// </summary>
    public static Vector4 Curve(in PostSettings post)
    {
        float aces = post.Tonemapper == Tonemapper.Aces ? 1f : 0f;
        return post.AgxLook == AgxLook.Base
            ? new Vector4(aces, 1f, 1f, 0f)
            : new Vector4(aces, 1.35f, 1.4f, 0f);
    }

    /// <summary>
    /// The white balance as a matrix on linear Rec.709 colour, for <see cref="Vector3.Transform(Vector3, Matrix4x4)"/>
    /// and, as uploaded, for <c>mat3(frame.whiteBalance) * color</c> in the shader. It adapts from the white
    /// the two settings pick to the neutral 6500 K by the Bradford method, the way a camera balances for
    /// the light. <paramref name="temperature"/> from -1 to 1 picks a white from 4000 K to 12000 K, evenly in
    /// mireds; <paramref name="tint"/> from -1 to 1 moves it 0.02 below or above the black-body line.
    /// Values beyond -1 and 1 are held there and ones that aren't numbers count as 0, so the shader never
    /// sees a broken matrix.
    /// </summary>
    public static Matrix4x4 WhiteBalance(float temperature, float tint)
    {
        double t = Settle(temperature);
        double neutralMired = 1e6 / NeutralKelvin;
        double endMired = 1e6 / (t >= 0.0 ? BluestKelvin : CoolestKelvin);
        double kelvin = 1e6 / (neutralMired + (Math.Abs(t) * (endMired - neutralMired)));

        var source = WhitePoint(kelvin, Settle(tint) * MaxDuv);
        var target = WhitePoint(NeutralKelvin, 0.0);

        // Into cone responses, scale each so the source white lands on the target, and back.
        var sourceCones = Multiply(Bradford, source);
        var targetCones = Multiply(Bradford, target);
        var scale = new double[3, 3];
        for (int i = 0; i < 3; i++)
        {
            scale[i, i] = targetCones[i] / sourceCones[i];
        }

        var adapt = Multiply(XyzToRec709, Multiply(BradfordInverse, Multiply(scale, Multiply(Bradford, Rec709ToXyz))));

        // System.Numerics multiplies a row vector on the left, so the matrix goes in transposed.
        return new Matrix4x4(
            (float)adapt[0, 0], (float)adapt[1, 0], (float)adapt[2, 0], 0f,
            (float)adapt[0, 1], (float)adapt[1, 1], (float)adapt[2, 1], 0f,
            (float)adapt[0, 2], (float)adapt[1, 2], (float)adapt[2, 2], 0f,
            0f, 0f, 0f, 1f);
    }

    private static double Settle(float value) => float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0.0;

    /// <summary>The XYZ, with Y of 1, of a black body at <paramref name="kelvin"/> moved <paramref name="duv"/> off its line, green side up.</summary>
    private static double[] WhitePoint(double kelvin, double duv)
    {
        var (u, v) = Planck(kelvin);
        var (u2, v2) = Planck(kelvin + 1.0);

        // Square to the line, on the green side, which is towards higher v.
        double du = u2 - u;
        double dv = v2 - v;
        double length = Math.Sqrt((du * du) + (dv * dv));
        u += duv * dv / length;
        v -= duv * du / length;

        double d = (2.0 * u) - (8.0 * v) + 4.0;
        double x = 3.0 * u / d;
        double y = 2.0 * v / d;
        return [x / y, 1.0, (1.0 - x - y) / y];
    }

    /// <summary>Krystek's fit of the black-body colours in the CIE 1960 uv plane, good from 1000 K to 15000 K.</summary>
    private static (double U, double V) Planck(double t)
    {
        double t2 = t * t;
        double u = (0.860117757 + (1.54118254e-4 * t) + (1.28641212e-7 * t2)) / (1.0 + (8.42420235e-4 * t) + (7.08145163e-7 * t2));
        double v = (0.317398726 + (4.22806245e-5 * t) + (4.20481691e-8 * t2)) / (1.0 - (2.89741816e-5 * t) + (1.61456053e-7 * t2));
        return (u, v);
    }

    private static readonly double[,] Bradford =
    {
        { 0.8951, 0.2664, -0.1614 },
        { -0.7502, 1.7135, 0.0367 },
        { 0.0389, -0.0685, 1.0296 },
    };

    private static readonly double[,] BradfordInverse =
    {
        { 0.9869929, -0.1470543, 0.1599627 },
        { 0.4323053, 0.5183603, 0.0492912 },
        { -0.0085287, 0.0400428, 0.9684867 },
    };

    private static readonly double[,] Rec709ToXyz =
    {
        { 0.4124564, 0.3575761, 0.1804375 },
        { 0.2126729, 0.7151522, 0.0721750 },
        { 0.0193339, 0.1191920, 0.9503041 },
    };

    private static readonly double[,] XyzToRec709 =
    {
        { 3.2404542, -1.5371385, -0.4985314 },
        { -0.9692660, 1.8760108, 0.0415560 },
        { 0.0556434, -0.2040259, 1.0572252 },
    };

    private static double[,] Multiply(double[,] a, double[,] b)
    {
        var result = new double[3, 3];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                result[i, j] = (a[i, 0] * b[0, j]) + (a[i, 1] * b[1, j]) + (a[i, 2] * b[2, j]);
            }
        }

        return result;
    }

    private static double[] Multiply(double[,] a, double[] v) =>
    [
        (a[0, 0] * v[0]) + (a[0, 1] * v[1]) + (a[0, 2] * v[2]),
        (a[1, 0] * v[0]) + (a[1, 1] * v[1]) + (a[1, 2] * v[2]),
        (a[2, 0] * v[0]) + (a[2, 1] * v[1]) + (a[2, 2] * v[2]),
    ];
}
