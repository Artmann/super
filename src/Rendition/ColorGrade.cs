using System.Numerics;

namespace Rendition;

/// <summary>
/// The finer colour work of <see cref="PostSettings.Grade"/>, past contrast, saturation, lift, gain and white balance: a
/// hue shift, a channel mixer, tints for the shadows, mid-tones and highlights, split toning and an artist's colour
/// table. Leaving a value at zero leaves that part unchanged. It is all baked into one table with the curve, so it costs
/// nothing a pixel. See docs/rendering.md.
/// </summary>
public struct ColorGrade
{
    /// <summary>
    /// Turns every colour round the colour wheel by this many degrees, with grey left grey: 20 moves reds towards
    /// orange and greens towards teal. 0 leaves the colours as they are.
    /// </summary>
    public float HueShift;

    /// <summary>
    /// The channel mixer's red: how much of the red, green and blue light is added to the red channel, on top of
    /// its own. (0, 0.1, 0) mixes a tenth of the green into the red; (-1, 1, 0) swaps green in for it. Zero leaves
    /// red as it is.
    /// </summary>
    public Vector3 RedFrom;

    /// <summary>The channel mixer's green: how much of the red, green and blue light is added to green. See <see cref="RedFrom"/>.</summary>
    public Vector3 GreenFrom;

    /// <summary>The channel mixer's blue: how much of the red, green and blue light is added to blue. See <see cref="RedFrom"/>.</summary>
    public Vector3 BlueFrom;

    /// <summary>
    /// Extra brightness in each of red, green and blue in the dark parts of the picture only, fading out by the
    /// mid-tones. (0, 0, 0.1) blues the shade; -0.1 deepens it. Black stays black: <see cref="PostSettings.Lift"/>
    /// raises it.
    /// </summary>
    public Vector3 Shadows;

    /// <summary>Extra brightness in each of red, green and blue in the mid-tones, where faces and grass sit. See <see cref="Shadows"/>.</summary>
    public Vector3 Midtones;

    /// <summary>
    /// Extra brightness in each of red, green and blue in the bright parts of the picture only, from the mid-tones
    /// up. (0.06, 0.03, 0) gives warm highlights. See <see cref="Shadows"/>.
    /// </summary>
    public Vector3 Highlights;

    /// <summary>
    /// Split toning's colour for the shade: the picture's darker half leans this way, with its brightness kept.
    /// (0, 0.04, 0.08) gives teal shade; zero leaves it.
    /// </summary>
    public Vector3 SplitShadows;

    /// <summary>
    /// Split toning's colour for the light: the picture's brighter half leans this way, with its brightness kept.
    /// (0.08, 0.04, 0) gives an orange glow; zero leaves it.
    /// </summary>
    public Vector3 SplitHighlights;

    /// <summary>
    /// Where split toning turns from <see cref="SplitShadows"/> to <see cref="SplitHighlights"/>, from -1 to 1.
    /// 0 turns at the middle of the brightness; towards 1 more of the picture counts as shade, towards -1 more as light.
    /// </summary>
    public float SplitBalance;

    /// <summary>
    /// An artist's colour lookup table, laid over the finished picture by <see cref="LutBlend"/>. A Super game loads
    /// one with <c>Content.Lut</c>, from a <c>.cube</c> file or a strip image graded from <c>--neutral-lut</c>.
    /// </summary>
    public ColorLut? Lut;

    /// <summary>
    /// How much of <see cref="Lut"/> shows, from 0, none, to 1, the table's own colours. 0.5 goes halfway.
    /// </summary>
    public float LutBlend;

    /// <summary>
    /// The grade <paramref name="t"/> of the way from <paramref name="from"/> to <paramref name="to"/>, as
    /// <see cref="PostSettings.Lerp"/> eases it. Two different <see cref="Lut"/>s cross through none: the first fades
    /// out over the first half and the second fades in over the second. <paramref name="t"/> is held from 0 to 1.
    /// </summary>
    public static ColorGrade Lerp(in ColorGrade from, in ColorGrade to, float t)
    {
        if (!(t > 0f))
        {
            return from;
        }

        if (t >= 1f)
        {
            return to;
        }

        bool sameLut = from.Lut == to.Lut;
        return new ColorGrade
        {
            HueShift = SettingsMix.Mix(from.HueShift, to.HueShift, t),
            RedFrom = Vector3.Lerp(from.RedFrom, to.RedFrom, t),
            GreenFrom = Vector3.Lerp(from.GreenFrom, to.GreenFrom, t),
            BlueFrom = Vector3.Lerp(from.BlueFrom, to.BlueFrom, t),
            Shadows = Vector3.Lerp(from.Shadows, to.Shadows, t),
            Midtones = Vector3.Lerp(from.Midtones, to.Midtones, t),
            Highlights = Vector3.Lerp(from.Highlights, to.Highlights, t),
            SplitShadows = Vector3.Lerp(from.SplitShadows, to.SplitShadows, t),
            SplitHighlights = Vector3.Lerp(from.SplitHighlights, to.SplitHighlights, t),
            SplitBalance = SettingsMix.Mix(from.SplitBalance, to.SplitBalance, t),
            Lut = sameLut || t < 0.5f ? from.Lut : to.Lut,
            LutBlend = sameLut
                ? SettingsMix.Mix(from.LutBlend, to.LutBlend, t)
                : t < 0.5f ? from.LutBlend * (1f - (2f * t)) : to.LutBlend * ((2f * t) - 1f),
        };
    }
}

/// <summary>The even mixes <see cref="PostSettings.Lerp"/> and <see cref="ColorGrade.Lerp"/> share.</summary>
internal static class SettingsMix
{
    /// <summary>Written so t = 0 gives from and t = 1 gives to exactly, with no rounding left over.</summary>
    public static float Mix(float from, float to, float t) => (from * (1f - t)) + (to * t);

    /// <summary>The value a setting whose 0 means a default stands for.</summary>
    public static float Or(float value, float otherwise) => value == 0f ? otherwise : value;
}
