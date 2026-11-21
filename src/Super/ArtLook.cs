using Rendition;

namespace Super;

/// <summary>
/// One named choice that sets the mood of the whole picture: a colour grade and screen effects laid over the game's own
/// finish, paint, retro pixels and nudges to the light. Pick one of <see cref="Looks"/>, change one with
/// <c>with { … }</c>, or make your own, and show it with <see cref="ArtStyle.Use"/>. Every number is a change, so the
/// default look, <see cref="Looks.Natural"/>, leaves the picture as the game made it. See "Looks" in docs/rendering.md.
/// </summary>
public readonly record struct ArtLook
{
    /// <summary>What the look is called, for menus and <c>--look</c>.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Laid over the game's finish: every number is added to the game's own, so a <see cref="PostSettings.Contrast"/>
    /// of 0.2 adds contrast to whatever the game set, and the results are held to their ranges. A colour table here
    /// replaces the game's, and each screen effect set here wins over the game's. The exposure meter, the tonemapper and
    /// the lens stay the game's.
    /// </summary>
    public PostSettings Finish { get; init; }

    /// <summary>The oil paint. With an amount above 0 it replaces any paint the game set; the default paints nothing.</summary>
    public PaintSettings Paint { get; init; }

    /// <summary>Big pixels, fewer shades and a pattern of dots. The default changes nothing.</summary>
    public RetroSettings Retro { get; init; }

    /// <summary>Nudges to the sun, the sky, the fog, the clouds and the lens flare. The default changes nothing.</summary>
    public LookLight Light { get; init; }

    /// <summary>
    /// The look <paramref name="t"/> of the way from <paramref name="from"/> to <paramref name="to"/>, for easing from
    /// one into the other. Numbers move evenly; the name and a colour table change halfway, and paint or retro pixels
    /// that only one side has grow in from nothing.
    /// </summary>
    public static ArtLook Lerp(in ArtLook from, in ArtLook to, float t)
    {
        if (!(t > 0f))
        {
            return from;
        }

        if (t >= 1f)
        {
            return to;
        }

        return new ArtLook
        {
            Name = t < 0.5f ? from.Name : to.Name,
            Finish = PostSettings.Lerp(from.Finish, to.Finish, t),
            Paint = LookMix.Paint(from.Paint, to.Paint, t),
            Retro = LookMix.Retro(from.Retro, to.Retro, t),
            Light = LookLight.Lerp(from.Light, to.Light, t),
        };
    }
}

/// <summary>
/// How a <see cref="ArtLook"/> changes the light, on top of what the game set. Every value is a change, and 0 leaves
/// that light as it is.
/// </summary>
public readonly record struct LookLight
{
    /// <summary>How much brighter the suns are, in stops: 1 is twice as bright, -1 half.</summary>
    public float Sun { get; init; }

    /// <summary>How much warmer the suns' colour is, from -1, a cold blue, to 1, a low orange sun. Their brightness stays.</summary>
    public float SunWarmth { get; init; }

    /// <summary>How much brighter the sky and the light it gives are, in stops.</summary>
    public float Sky { get; init; }

    /// <summary>
    /// Fog added to the sky's, in light taken per metre: 0.01 is a light haze, 0.05 a thick fog. A sky without fog gets
    /// it near the ground, thinning upwards.
    /// </summary>
    public float Fog { get; init; }

    /// <summary>
    /// Cloud cover added to the sky's, from -1 to 1: 0.4 turns a fair sky cloudy. A sky without clouds gets fair-weather
    /// clouds this thick. Only a physical sky has clouds.
    /// </summary>
    public float Clouds { get; init; }

    /// <summary>How much brighter the lens flare is, in stops: -10 all but hides it.</summary>
    public float Flare { get; init; }

    /// <summary>The light <paramref name="t"/> of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static LookLight Lerp(in LookLight from, in LookLight to, float t) => new()
    {
        Sun = Mix(from.Sun, to.Sun, t),
        SunWarmth = Mix(from.SunWarmth, to.SunWarmth, t),
        Sky = Mix(from.Sky, to.Sky, t),
        Fog = Mix(from.Fog, to.Fog, t),
        Clouds = Mix(from.Clouds, to.Clouds, t),
        Flare = Mix(from.Flare, to.Flare, t),
    };

    private static float Mix(float from, float to, float t) => LookMix.Mix(from, to, Math.Clamp(t, 0f, 1f));
}
