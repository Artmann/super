using System.Numerics;
using Rendition;

namespace Super;

/// <summary>The mixes <see cref="ArtLook.Lerp"/> needs for paint and retro pixels, whose zeros stand for none.</summary>
internal static class LookMix
{
    /// <summary>Written so t = 0 gives from and t = 1 gives to exactly.</summary>
    public static float Mix(float from, float to, float t) => (from * (1f - t)) + (to * t);

    /// <summary>The paint between two, where a side that paints nothing takes the other's brush and only its amount grows.</summary>
    public static PaintSettings Paint(in PaintSettings from, in PaintSettings to, float t)
    {
        var brush = from.Amount == 0f ? to : to.Amount == 0f ? from : default;
        if (from.Amount == 0f || to.Amount == 0f)
        {
            return brush with { Amount = Mix(from.Amount, to.Amount, t) };
        }

        return new PaintSettings
        {
            Amount = Mix(from.Amount, to.Amount, t),
            BrushSize = Mix(Or(from.BrushSize, PaintSettings.DefaultBrushSize), Or(to.BrushSize, PaintSettings.DefaultBrushSize), t),
            Sharpness = Mix(from.Sharpness ?? PaintSettings.DefaultSharpness, to.Sharpness ?? PaintSettings.DefaultSharpness, t),
            Near = Mix(from.Near, to.Near, t),
            Far = Mix(from.Far, to.Far, t),
        };
    }

    /// <summary>
    /// The retro pixels between two: the pixels grow from 1 and the shades thin from 256, and a side that keeps every
    /// shade takes the other's dither.
    /// </summary>
    public static RetroSettings Retro(in RetroSettings from, in RetroSettings to, float t)
    {
        float fromLevels = from.Levels > 0 ? from.Levels : 256f;
        float toLevels = to.Levels > 0 ? to.Levels : 256f;
        int levels = (int)MathF.Round(Mix(fromLevels, toLevels, t));
        return new RetroSettings
        {
            PixelSize = Mix(MathF.Max(from.PixelSize, 1f), MathF.Max(to.PixelSize, 1f), t),
            Levels = levels >= 256 ? 0 : levels,
            Dither = Mix(from.Levels > 0 ? from.Dither : to.Dither, to.Levels > 0 ? to.Dither : from.Dither, t),
        };
    }

    private static float Or(float value, float otherwise) => value == 0f ? otherwise : value;
}

/// <summary>
/// Lays a <see cref="ArtLook"/> over what the game set for a frame: its finish, paint, suns, sky, fog, clouds and lens
/// flare. Each is held to its range, so a look added to a game's settings can't push them past it.
/// </summary>
internal static class LookLayer
{
    /// <summary>The sun's colour at full warmth and full cold, before their brightness is put back.</summary>
    private static readonly Vector3 Warm = new(1f, 0.72f, 0.45f);
    private static readonly Vector3 Cold = new(0.7f, 0.85f, 1.15f);

    /// <summary>The look's finish added to the game's; see <see cref="ArtLook.Finish"/>.</summary>
    public static PostSettings Finish(in PostSettings game, in ArtLook look)
    {
        var add = look.Finish;
        return game with
        {
            Exposure = game.Exposure + add.Exposure,
            Contrast = MathF.Max(game.Contrast + add.Contrast, -0.9f),
            Saturation = MathF.Max(game.Saturation + add.Saturation, -1f),
            Vignette = Math.Clamp(game.Vignette + add.Vignette, 0f, 1f),
            Sharpen = Math.Clamp(game.Sharpen + add.Sharpen, 0f, 1f),
            Grain = MathF.Max(game.Grain + add.Grain, 0f),
            Bloom = game.Bloom + add.Bloom,
            Lift = game.Lift + add.Lift,
            Gain = game.Gain + add.Gain,
            Temperature = Math.Clamp(game.Temperature + add.Temperature, -1f, 1f),
            Tint = Math.Clamp(game.Tint + add.Tint, -1f, 1f),
            Grade = Grade(game.Grade, add.Grade),
            Screen = ScreenEffects.Over(add.Screen, game.Screen),
        };
    }

    /// <summary>
    /// The paint between the game's and the look's: the look's takes over as its amount rises to 1, so a look easing in
    /// grows from the game's paint rather than replacing it at once. A game that paints nothing gets the look's as it is.
    /// </summary>
    public static PaintSettings Paint(in PaintSettings game, in ArtLook look)
    {
        float weight = Math.Clamp(look.Paint.Amount, 0f, 1f);
        if (weight == 0f)
        {
            return game;
        }

        if (weight == 1f || game.Amount == 0f)
        {
            return look.Paint;
        }

        return LookMix.Paint(game, look.Paint, weight);
    }

    /// <summary>A sun's colour, brighter and warmer or colder as the look asks.</summary>
    public static Vector3 Sun(Vector3 color, in LookLight light)
    {
        if (light.Sun == 0f && light.SunWarmth == 0f)
        {
            return color;
        }

        float warmth = Math.Clamp(light.SunWarmth, -1f, 1f);
        var tint = warmth >= 0f ? Vector3.Lerp(Vector3.One, Warm, warmth) : Vector3.Lerp(Vector3.One, Cold, -warmth);
        // The tint keeps the sun's brightness, so only Sun changes how much light it gives.
        tint /= Vector3.Dot(tint, new Vector3(0.2126f, 0.7152f, 0.0722f));
        return color * tint * MathF.Pow(2f, light.Sun);
    }

    /// <summary>The sky's brightness, as many stops brighter as the look asks.</summary>
    public static float Sky(float intensity, in LookLight light) => light.Sky == 0f ? intensity : intensity * MathF.Pow(2f, light.Sky);

    /// <summary>The fog with the look's added to it.</summary>
    public static FogSettings Fog(FogSettings fog, in LookLight light) =>
        light.Fog == 0f ? fog : fog with { Density = MathF.Max(fog.Density + light.Fog, 0f) };

    /// <summary>
    /// The clouds with the look's cover added; none stays none. A sky without clouds gets fair-weather ones that grow
    /// from nothing: their cirrus thickens with the cover until it is fair weather's at fair weather's cover.
    /// </summary>
    public static CloudSettings? Clouds(CloudSettings? clouds, in LookLight light)
    {
        if (light.Clouds == 0f || clouds is not { } sky)
        {
            return clouds;
        }

        if (sky.Any)
        {
            return sky with { Coverage = Math.Clamp(sky.Coverage + light.Clouds, 0f, 1f) };
        }

        var fair = CloudSettings.Fair;
        float cover = Math.Clamp(light.Clouds, 0f, 1f);
        return fair with { Coverage = cover, Cirrus = fair.Cirrus * MathF.Min(cover / fair.Coverage, 1f) };
    }

    /// <summary>The lens flare, as many stops brighter as the look asks.</summary>
    public static LensFlareSettings Flare(in LensFlareSettings flare, in LookLight light) =>
        light.Flare == 0f ? flare : flare with { Intensity = (flare.Intensity ?? 1f) * MathF.Pow(2f, light.Flare) };

    // Each part of the grade added to the game's, with the look's colour table, when it has one, in place of the game's.
    private static ColorGrade Grade(in ColorGrade game, in ColorGrade add) => new()
    {
        HueShift = game.HueShift + add.HueShift,
        RedFrom = game.RedFrom + add.RedFrom,
        GreenFrom = game.GreenFrom + add.GreenFrom,
        BlueFrom = game.BlueFrom + add.BlueFrom,
        Shadows = game.Shadows + add.Shadows,
        Midtones = game.Midtones + add.Midtones,
        Highlights = game.Highlights + add.Highlights,
        SplitShadows = game.SplitShadows + add.SplitShadows,
        SplitHighlights = game.SplitHighlights + add.SplitHighlights,
        SplitBalance = Math.Clamp(game.SplitBalance + add.SplitBalance, -1f, 1f),
        Lut = add.Lut ?? game.Lut,
        LutBlend = add.Lut is null ? game.LutBlend : add.LutBlend,
    };
}
