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
    /// the sharpening, with <see cref="RenderEffects.ColorGrading"/> the contrast, saturation, lift, gain, white
    /// balance, hue shift, channel mixer, tones and split toning, and with <see cref="RenderEffects.ColorLut"/> the
    /// artist's table. The exposure, the bloom, the tonemapper and the lens are left alone.
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
            // The artist's table has its own switch.
            post.Grade = new ColorGrade { Lut = post.Grade.Lut, LutBlend = post.Grade.LutBlend };
        }

        // A table that doesn't show is no table, so the bake never reads one it would mix in none of.
        if ((off & RenderEffects.ColorLut) != 0 || post.Grade.Lut is null || !(post.Grade.LutBlend > 0f))
        {
            post.Grade.Lut = null;
            post.Grade.LutBlend = 0f;
        }

        post.Screen = ScreenWithout(post.Screen, off);
        return post;
    }

    /// <summary>
    /// <paramref name="screen"/> with what <paramref name="off"/> leaves out set back to none: everything but the fade
    /// with <see cref="RenderEffects.ScreenEffects"/>, and each of the fringes, the speed blur and the flash with its own.
    /// </summary>
    private static ScreenEffects ScreenWithout(ScreenEffects screen, RenderEffects off)
    {
        if ((off & RenderEffects.ScreenEffects) != 0)
        {
            return new ScreenEffects { Fade = screen.Fade, FadeColor = screen.FadeColor };
        }

        if ((off & RenderEffects.ChromaticAberration) != 0)
        {
            screen.ChromaticAberration = 0f;
        }

        if ((off & RenderEffects.SpeedBlur) != 0)
        {
            screen.SpeedBlur = 0f;
        }

        if ((off & RenderEffects.Flashes) != 0)
        {
            screen.Flash = 0f;
        }

        return screen;
    }

    /// <summary>
    /// What the finish passes read of the screen effects. The fringes and the speed blur are worked out in the tonemap
    /// pass, on the scene's light; the rest in the finish pass, on the colours the screen shows. Each amount is held
    /// from 0 to 1, so a stray value can't turn the picture inside out. <paramref name="canvasSlot"/> is the
    /// renderer's slot for <see cref="ScreenEffects.Canvas"/>, -1 for none.
    /// </summary>
    public static FrameScreenBlock Screen(in ScreenEffects screen, int canvasSlot)
    {
        var center = screen.SpeedBlurCenter ?? new Vector2(0.5f);
        float canvas = canvasSlot < 0 ? 0f : Math.Clamp(screen.CanvasStrength, 0f, 1f);
        var flashColor = screen.FlashColor == Vector3.Zero ? Vector3.One : screen.FlashColor;
        return new FrameScreenBlock
        {
            ScreenLens = new Vector4(Math.Clamp(screen.ChromaticAberration, 0f, 1f), Math.Clamp(screen.SpeedBlur, 0f, 1f), center.X, center.Y),
            ScreenFlash = new Vector4(Vector3.Clamp(flashColor, Vector3.Zero, Vector3.One), Math.Clamp(screen.Flash, 0f, 1f)),
            ScreenFade = new Vector4(Vector3.Clamp(screen.FadeColor, Vector3.Zero, Vector3.One), Math.Clamp(screen.Fade, 0f, 1f)),
            EdgeTint = Edge(screen.EdgeTint),
            SecondEdgeTint = Edge(screen.SecondEdgeTint),
            ScreenCanvas = new Vector4(
                Math.Clamp(screen.Desaturate, 0f, 1f), canvas, Math.Max(screen.CanvasTileSize, 0f), canvas > 0f ? canvasSlot : -1),
        };

        static Vector4 Edge(EdgeTint tint) =>
            new(Vector3.Clamp(tint.Color, Vector3.Zero, Vector3.One), Math.Clamp(tint.Strength, 0f, 1f));
    }

    /// <summary>
    /// What the grade's table is baked from, besides <see cref="FrameGradeBlock"/>'s contrast, saturation, lift and
    /// gain: the colour matrix, the curve, the five tones and how much of the artist's table shows.
    /// </summary>
    public static FrameToneBlock Tone(in PostSettings post) => new()
    {
        ColorMatrix = ColorMatrix(post),
        Curve = Curve(post),
        Look = new Vector4(
            Math.Clamp(post.Grade.SplitBalance, -1f, 1f),
            post.Grade.Shadows == Vector3.Zero && post.Grade.Midtones == Vector3.Zero && post.Grade.Highlights == Vector3.Zero
                && post.Grade.SplitShadows == Vector3.Zero && post.Grade.SplitHighlights == Vector3.Zero ? 0f : 1f,
            0f, 0f),
        SplitShadows = new Vector4(post.Grade.SplitShadows, 0f),
        SplitHighlights = new Vector4(post.Grade.SplitHighlights, 0f),
        Shadows = new Vector4(post.Grade.Shadows, 0f),
        Midtones = new Vector4(post.Grade.Midtones, 0f),
        Highlights = new Vector4(post.Grade.Highlights, 0f),
    };

    /// <summary>
    /// The white balance, then the channel mixer, then the hue shift, as one matrix on linear colour, laid out as
    /// <see cref="WhiteBalance"/> is. With all three at zero it is the identity.
    /// </summary>
    public static Matrix4x4 ColorMatrix(in PostSettings post) =>
        WhiteBalance(post.Temperature, post.Tint) * Mixer(post) * HueRotation(post.Grade.HueShift);

    /// <summary>
    /// The channel mixer, for a row vector on the left: red comes out as itself plus
    /// <see cref="ColorGrade.RedFrom"/>'s share of each channel, and so on.
    /// </summary>
    public static Matrix4x4 Mixer(in PostSettings post)
    {
        var (red, green, blue) = (post.Grade.RedFrom, post.Grade.GreenFrom, post.Grade.BlueFrom);
        return new Matrix4x4(
            1f + red.X, green.X, blue.X, 0f,
            red.Y, 1f + green.Y, blue.Y, 0f,
            red.Z, green.Z, 1f + blue.Z, 0f,
            0f, 0f, 0f, 1f);
    }

    /// <summary>
    /// A turn of <paramref name="degrees"/> about the grey axis, for a row vector on the left, so grey stays grey and
    /// a positive turn moves red towards yellow. Not a number counts as 0.
    /// </summary>
    public static Matrix4x4 HueRotation(float degrees)
    {
        if (!float.IsFinite(degrees) || degrees == 0f)
        {
            return Matrix4x4.Identity;
        }

        // Rodrigues' rotation about (1, 1, 1) / sqrt(3).
        double angle = degrees * Math.PI / 180.0;
        double cos = Math.Cos(angle);
        double across = (1.0 - cos) / 3.0;
        double turn = Math.Sin(angle) / Math.Sqrt(3.0);
        float diagonal = (float)(cos + across);
        float ahead = (float)(across + turn);
        float behind = (float)(across - turn);

        // Column-vector form: red's column gains green and loses blue. Stored transposed for the row vector.
        return new Matrix4x4(
            diagonal, ahead, behind, 0f,
            behind, diagonal, ahead, 0f,
            ahead, behind, diagonal, 0f,
            0f, 0f, 0f, 1f);
    }

    /// <summary>The lift, with the bloom's strength in w, and the gain as a multiplier for each channel.</summary>
    public static (Vector4 Lift, Vector4 Gain) Tint(in PostSettings post)
    {
        float bloom = Math.Clamp(DefaultBloom + post.Bloom, 0f, 1f);
        var gain = Vector3.Max(Vector3.One + post.Gain, Vector3.Zero);
        return (new Vector4(post.Lift, bloom), new Vector4(gain, 0f));
    }

    /// <summary>
    /// Which curve the grade's bake uses, as <c>frame.curve</c>: x is 0 for AgX, 1 for ACES and 2 for Khronos PBR
    /// Neutral; y and z are the AgX look's power and saturation, applied to the curve's display values; w is how much
    /// of the artist's table shows, 0 to 1. Blender's Punchy look is a power of 1.35 and a saturation of 1.4; the base
    /// look is 1 and 1.
    /// </summary>
    public static Vector4 Curve(in PostSettings post)
    {
        float curve = post.Tonemapper switch
        {
            Tonemapper.Aces => 1f,
            Tonemapper.Neutral => 2f,
            _ => 0f,
        };
        float blend = post.Grade.Lut is null ? 0f : Math.Clamp(post.Grade.LutBlend, 0f, 1f);
        return post.AgxLook == AgxLook.Base
            ? new Vector4(curve, 1f, 1f, blend)
            : new Vector4(curve, 1.35f, 1.4f, blend);
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
