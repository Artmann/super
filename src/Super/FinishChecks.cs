using System.Globalization;
using System.Numerics;
using Rendition;

namespace Super;

/// <summary>Refuses a finish or screen effects that can't be drawn, with a message saying which setting and why.</summary>
internal static class FinishChecks
{
    /// <summary>The hue shift, the split toning's balance and the colour table's blend.</summary>
    public static void Grade(in PostSettings settings)
    {
        if (!float.IsFinite(settings.Grade.HueShift))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                string.Create(CultureInfo.InvariantCulture, $"The hue shift must be a number of degrees, but it is {settings.Grade.HueShift}."));
        }

        if (!(settings.Grade.SplitBalance is >= -1f and <= 1f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                string.Create(CultureInfo.InvariantCulture, $"The split toning balance must be from -1 to 1, with 0 for the middle, but it is {settings.Grade.SplitBalance}."));
        }

        if (!(settings.Grade.LutBlend is >= 0f and <= 1f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(settings),
                string.Create(CultureInfo.InvariantCulture, $"The colour table's blend must be from 0, none, to 1, all of it, but it is {settings.Grade.LutBlend}."));
        }
    }

    /// <summary>
    /// The adaptation speeds at 0 or above, the centre weight from 0 to 1, and the limits numbers with the darkest
    /// not past the brightest.
    /// </summary>
    public static void Metering(in PostSettings settings)
    {
        // Each message is built only when it is thrown, since this runs every frame.
        if (!(float.IsFinite(settings.AdaptationSpeed) && settings.AdaptationSpeed >= 0f))
        {
            throw Refusal(string.Create(CultureInfo.InvariantCulture,
                $"The adaptation speed must be above 0 stops a second, or 0 for the default of 2, but it is {settings.AdaptationSpeed}."));
        }

        var metering = settings.Metering;
        if (!(float.IsFinite(metering.DarkSpeed) && metering.DarkSpeed >= 0f))
        {
            throw Refusal(string.Create(CultureInfo.InvariantCulture,
                $"The speed into the dark must be above 0 stops a second, or 0 for the default of 1, but it is {metering.DarkSpeed}."));
        }

        if (metering.Centre is not (>= 0f and <= 1f))
        {
            throw Refusal(string.Create(CultureInfo.InvariantCulture,
                $"The metering's centre weight must be from 0, every pixel the same, to 1, but it is {metering.Centre}."));
        }

        MeteringLimits(metering);
    }

    private static void MeteringLimits(in MeteringSettings metering)
    {
        if (metering.Darkest is { } darkest && !float.IsFinite(darkest))
        {
            throw Refusal(string.Create(CultureInfo.InvariantCulture,
                $"The darkest scene to meter for must be a number of stops, or null for no limit, but it is {darkest}."));
        }

        if (metering.Brightest is { } brightest && !float.IsFinite(brightest))
        {
            throw Refusal(string.Create(CultureInfo.InvariantCulture,
                $"The brightest scene to meter for must be a number of stops, or null for no limit, but it is {brightest}."));
        }

        if (metering.Darkest > metering.Brightest)
        {
            throw Refusal(string.Create(CultureInfo.InvariantCulture,
                $"The darkest scene to meter for, {metering.Darkest}, can't be brighter than the brightest, {metering.Brightest}."));
        }
    }

    /// <summary>A refusal of the settings passed to <see cref="ViewEffects.Finish"/>.</summary>
    private static ArgumentOutOfRangeException Refusal(string message) => new("settings", message);

    /// <summary>Every amount from 0 to 1, the colours and the speed blur's centre numbers, and the paper's tile not below 0.</summary>
    public static void Screen(in ScreenEffects effects, string parameter)
    {
        Amount(effects.ChromaticAberration, "chromatic aberration", parameter);
        Amount(effects.SpeedBlur, "speed blur", parameter);
        Amount(effects.Flash, "flash", parameter);
        Amount(effects.Fade, "fade", parameter);
        Amount(effects.EdgeTint.Strength, "edge tint's strength", parameter);
        Amount(effects.SecondEdgeTint.Strength, "second edge tint's strength", parameter);
        Amount(effects.Desaturate, "desaturation", parameter);
        Amount(effects.Blur, "blur", parameter);
        Amount(effects.CanvasStrength, "canvas strength", parameter);
        if (!(float.IsFinite(effects.CanvasTileSize) && effects.CanvasTileSize >= 0f))
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                string.Create(CultureInfo.InvariantCulture,
                    $"The canvas tile size must be a number of pixels, or 0 for the texture's own size, but it is {effects.CanvasTileSize}."));
        }

        if (effects.SpeedBlurCenter is { } center && !(float.IsFinite(center.X) && float.IsFinite(center.Y)))
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                string.Create(CultureInfo.InvariantCulture, $"The speed blur's centre must be a place on the screen, but it is {center}."));
        }

        Color(effects.FlashColor, "flash", parameter);
        Color(effects.FadeColor, "fade", parameter);
        Color(effects.EdgeTint.Color, "edge tint", parameter);
        Color(effects.SecondEdgeTint.Color, "second edge tint", parameter);
    }

    private static void Amount(float value, string name, string parameter)
    {
        if (!(value is >= 0f and <= 1f))
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                string.Create(CultureInfo.InvariantCulture, $"The {name} must be from 0, none, to 1, but it is {value}."));
        }
    }

    private static void Color(Vector3 color, string name, string parameter)
    {
        if (!(float.IsFinite(color.X) && float.IsFinite(color.Y) && float.IsFinite(color.Z)))
        {
            throw new ArgumentOutOfRangeException(
                parameter,
                string.Create(CultureInfo.InvariantCulture, $"The {name} colour must be numbers, but it is {color}."));
        }
    }
}
