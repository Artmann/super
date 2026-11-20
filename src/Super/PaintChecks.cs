using System.Globalization;
using Rendition;

namespace Super;

/// <summary>Refuses paint that can't be drawn, with a message saying which setting and why.</summary>
internal static class PaintChecks
{
    public static void Settings(in PaintSettings paint, string parameter)
    {
        if (!(paint.Amount is >= 0f and <= 1f))
        {
            throw Refuse(parameter, $"The paint's amount must be from 0 to 1, with 0 for none, but it is {paint.Amount}.");
        }

        if (!(paint.BrushSize is >= 0f and <= PaintSettings.MaxBrushSize))
        {
            throw Refuse(parameter,
                $"The paint's brush size must be from 0 to {PaintSettings.MaxBrushSize} pixels, with 0 for the default of {PaintSettings.DefaultBrushSize}, but it is {paint.BrushSize}.");
        }

        if (paint.Sharpness is { } sharpness && !(sharpness is >= 0f and <= 1f))
        {
            throw Refuse(parameter, $"The paint's sharpness must be from 0 to 1, or null for the default of {PaintSettings.DefaultSharpness}, but it is {sharpness}.");
        }

        Distances(paint, parameter);
    }

    private static void Distances(in PaintSettings paint, string parameter)
    {
        if (!(float.IsFinite(paint.Near) && paint.Near >= 0f))
        {
            throw Refuse(parameter, $"The paint's near distance must be 0 metres or more, but it is {paint.Near}.");
        }

        if (!(float.IsFinite(paint.Far) && paint.Far >= 0f))
        {
            throw Refuse(parameter, $"The paint's far distance must be 0 metres or more, with 0 to paint the whole picture, but it is {paint.Far}.");
        }

        if (paint.Far > 0f && paint.Far <= paint.Near)
        {
            throw Refuse(parameter, $"The paint's far distance, {paint.Far} metres, must be past its near distance, {paint.Near} metres.");
        }
    }

    private static ArgumentOutOfRangeException Refuse(string parameter, FormattableString message) =>
        new(parameter, message.ToString(CultureInfo.InvariantCulture));
}
