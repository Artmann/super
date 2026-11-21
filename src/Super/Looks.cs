using System.Numerics;
using Rendition;

namespace Super;

/// <summary>
/// The looks Super comes with, for <see cref="ArtStyle.Use"/>. Each is a plain <see cref="ArtLook"/>, so
/// <c>Looks.Noir with { Finish = Looks.Noir.Finish with { Grain = 0f } }</c> is Noir without its grain. See "Looks" in
/// docs/rendering.md for a picture of each.
/// </summary>
public static class Looks
{
    /// <summary>The game's own picture, unchanged.</summary>
    public static ArtLook Natural { get; } = new() { Name = "Natural" };

    /// <summary>An oil painting in warm afternoon light: broad, crisp brush strokes and gentle, rich colour.</summary>
    public static ArtLook Painterly { get; } = new()
    {
        Name = "Painterly",
        Finish = new PostSettings
        {
            Saturation = 0.25f,
            Contrast = 0.12f,
            Temperature = 0.18f,
            Tint = 0.04f,
            Sharpen = 0.3f,
            Vignette = 0.15f,
            Gain = new Vector3(0.04f, 0.02f, -0.03f),
            Grade = new ColorGrade
            {
                SplitShadows = new Vector3(0.02f, 0f, 0.06f),
                SplitHighlights = new Vector3(0.1f, 0.05f, 0f),
            },
        },
        Paint = new PaintSettings { Amount = 1f, BrushSize = 16f, Sharpness = 1f },
        Light = new LookLight { SunWarmth = 0.45f },
    };

    /// <summary>Sweet and bright: soft pastel colour, pink light, mint shade and a dreamy glow.</summary>
    public static ArtLook Candy { get; } = new()
    {
        Name = "Candy",
        Finish = new PostSettings
        {
            Exposure = 0.55f,
            Saturation = 0.6f,
            Contrast = -0.06f,
            Tint = 0.1f,
            Bloom = 0.12f,
            Sharpen = -0.3f,
            Lift = new Vector3(0.03f, 0.02f, 0.04f),
            Gain = new Vector3(0.06f, 0.02f, 0.06f),
            Grade = new ColorGrade
            {
                SplitShadows = new Vector3(0.02f, 0.06f, 0.1f),
                SplitHighlights = new Vector3(0.2f, 0.04f, 0.14f),
            },
            Vignette = -0.2f,
        },
        Light = new LookLight { Sun = 0.2f },
    };

    /// <summary>The hour before sunset on film: a low orange sun, teal shade, a glowing flare and darker corners.</summary>
    public static ArtLook GoldenHour { get; } = new()
    {
        Name = "Golden hour",
        Finish = new PostSettings
        {
            Temperature = 0.1f,
            Tint = 0.06f,
            Contrast = 0.1f,
            Saturation = 0.1f,
            Bloom = 0.08f,
            Vignette = 0.15f,
            Grade = new ColorGrade
            {
                SplitShadows = new Vector3(0f, 0.04f, 0.08f),
                SplitHighlights = new Vector3(0.14f, 0.06f, 0f),
            },
        },
        Light = new LookLight { SunWarmth = 1f, Sky = -0.5f, Fog = 0.003f, Flare = 1f },
    };

    /// <summary>Black and white crime film: hard contrast, deep corners and grain.</summary>
    public static ArtLook Noir { get; } = new()
    {
        Name = "Noir",
        Finish = new PostSettings
        {
            Saturation = -1f,
            Contrast = 0.35f,
            Exposure = -0.2f,
            Grain = 0.04f,
            Vignette = 0.35f,
        },
        Light = new LookLight { Sun = 0.3f },
    };

    /// <summary>Something is wrong here: dark and sickly green under heavy cloud and a low fog, grain and a shaky lens.</summary>
    public static ArtLook Horror { get; } = new()
    {
        Name = "Horror",
        Finish = new PostSettings
        {
            Exposure = -0.2f,
            Saturation = -0.45f,
            Contrast = 0.3f,
            Temperature = -0.2f,
            Tint = -0.15f,
            Lift = new Vector3(0f, 0.012f, 0.006f),
            Grain = 0.05f,
            Vignette = 0.55f,
            Grade = new ColorGrade { SplitShadows = new Vector3(0f, 0.04f, 0.03f) },
            Screen = new ScreenEffects { ChromaticAberration = 0.15f },
        },
        Light = new LookLight { Sun = -1f, Sky = -0.8f, Fog = 0.012f, Clouds = 0.7f, Flare = -10f },
    };

    /// <summary>A worn home video: colour bleeding at the edges, soft big pixels, noise, faded colour and a warm cast.</summary>
    public static ArtLook Vhs { get; } = new()
    {
        Name = "VHS",
        Finish = new PostSettings
        {
            Saturation = -0.25f,
            Contrast = -0.12f,
            Temperature = 0.12f,
            Tint = 0.04f,
            Lift = new Vector3(0.06f, 0.05f, 0.04f),
            Grain = 0.12f,
            Bloom = 0.06f,
            Sharpen = -1f,
            Vignette = 0.2f,
            Screen = new ScreenEffects { ChromaticAberration = 1f },
        },
        Retro = new RetroSettings { PixelSize = 3f, Levels = 40, Dither = 0.6f },
    };

    /// <summary>A 1990s console: big square pixels, few shades with a dither between them and no smoothing.</summary>
    public static ArtLook Psx { get; } = new()
    {
        Name = "PSX",
        Finish = new PostSettings { Saturation = 0.1f, Contrast = 0.05f, Sharpen = -0.4f },
        Retro = new RetroSettings { PixelSize = 4f, Levels = 32, Dither = 1f },
    };

    /// <summary>Every look above, in order, for a menu or a key that steps through them.</summary>
    public static IReadOnlyList<ArtLook> All { get; } = [Natural, Painterly, Candy, GoldenHour, Noir, Horror, Vhs, Psx];

    /// <summary>
    /// The look called <paramref name="name"/>, ignoring case, spaces and dashes, so <c>golden-hour</c> finds
    /// <see cref="GoldenHour"/>. Throws naming every look when none is called that.
    /// </summary>
    public static ArtLook Named(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        string wanted = Key(name);
        foreach (var look in All)
        {
            if (Key(look.Name!) == wanted)
            {
                return look;
            }
        }

        throw new ArgumentException(
            $"There is no look called '{name}'. The looks are {string.Join(", ", All.Select(look => ArgumentName(look.Name!)))}.",
            nameof(name));
    }

    /// <summary>How <c>--look</c> writes a look's name: lower case, with dashes for spaces.</summary>
    internal static string ArgumentName(string name) => name.Replace(' ', '-').ToLowerInvariant();

    private static string Key(string name) => name.Replace(" ", "", StringComparison.Ordinal)
        .Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
}
