using static Rendition.SettingsMix;

namespace Rendition;

/// <summary>
/// How <see cref="PostSettings.AutoExposure"/> meters the picture: how fast it follows the scene getting darker, how
/// far it may brighten or darken it, and how much the middle of the screen counts. Left at zero it adapts to the dark
/// at 1 stop a second, follows any scene and counts every pixel the same. See docs/rendering.md.
/// </summary>
public struct MeteringSettings
{
    /// <summary>
    /// How fast the exposure follows the scene getting darker, in stops a second. 0 means the default of 1, slower
    /// than <see cref="PostSettings.AdaptationSpeed"/>'s 2 for getting brighter, since eyes take longer to adjust to
    /// the dark than to the light.
    /// </summary>
    public float DarkSpeed;

    /// <summary>
    /// The darkest scene the camera brightens for, as the metered brightness in stops from 1: at -3, a scene whose
    /// middle is darker than 1/8 is brightened only as much as one of 1/8, so a cave stays dark. Null for no limit.
    /// </summary>
    public float? Darkest;

    /// <summary>
    /// The brightest scene the camera darkens for, as the metered brightness in stops from 1: at 2, a scene whose
    /// middle is brighter than 4 is darkened only as much as one of 4, so a snowfield stays bright. Null for no limit.
    /// </summary>
    public float? Brightest;

    /// <summary>
    /// How much more the middle of the screen counts than the edges, from 0 to 1: at 1 the very middle counts 16
    /// times as much as the corners, so a lit doorway ahead sets the exposure more than a dark wall at the side.
    /// </summary>
    public float Centre;

    /// <summary>
    /// The metering <paramref name="t"/> of the way from <paramref name="from"/> to <paramref name="to"/>. Speeds and
    /// the centre weight move evenly, and so does a limit both ends have. A limit only one end has changes at the
    /// halfway point.
    /// </summary>
    public static MeteringSettings Lerp(in MeteringSettings from, in MeteringSettings to, float t) => new()
    {
        DarkSpeed = Mix(Or(from.DarkSpeed, 1f), Or(to.DarkSpeed, 1f), t),
        Darkest = MixLimit(from.Darkest, to.Darkest, t),
        Brightest = MixLimit(from.Brightest, to.Brightest, t),
        Centre = Mix(from.Centre, to.Centre, t),
    };

    private static float? MixLimit(float? from, float? to, float t) =>
        from is { } a && to is { } b ? Mix(a, b, t) : t < 0.5f ? from : to;
}
