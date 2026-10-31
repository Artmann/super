using System.Globalization;

namespace Super;

/// <summary>
/// A color in sRGB from 0 to 1, as written in an image editor. Make one from whichever
/// notation suits: <see cref="Hex(string, float?)"/>, <see cref="Rgb"/>, <see cref="Hsl"/>
/// or <see cref="Oklch"/>. Use <c>color with { A = 0.5f }</c> to change just the opacity.
/// </summary>
public readonly record struct Color(float R, float G, float B, float A = 1f)
{
    public static Color White { get; } = new(1f, 1f, 1f);

    public static Color Black { get; } = new(0f, 0f, 0f);

    /// <summary>Reads a color written as 0xRRGGBB.</summary>
    public static Color Hex(int rgb, float alpha = 1f) =>
        Rgb((rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF, alpha);

    /// <summary>
    /// Reads a color written as #RGB, #RGBA, #RRGGBB or #RRGGBBAA, with or without the #.
    /// An alpha passed here wins over one in the text.
    /// </summary>
    public static Color Hex(string hex, float? alpha = null)
    {
        var digits = hex.AsSpan();
        if (digits.StartsWith("#"))
        {
            digits = digits[1..];
        }

        if (digits.Length is not (3 or 4 or 6 or 8)
            || !uint.TryParse(digits, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint value))
        {
            throw new FormatException($"'{hex}' isn't a hex color. Write it as #RGB, #RGBA, #RRGGBB or #RRGGBBAA.");
        }

        bool isShort = digits.Length <= 4;
        int channels = digits.Length is 4 or 8 ? 4 : 3;
        int bits = isShort ? 4 : 8;
        float largest = isShort ? 0xF : 0xFF;

        float Channel(int index) => ((value >> ((channels - 1 - index) * bits)) & (uint)largest) / largest;

        return new Color(Channel(0), Channel(1), Channel(2), alpha ?? (channels == 4 ? Channel(3) : 1f));
    }

    /// <summary>Red, green and blue from 0 to 255, as in CSS.</summary>
    public static Color Rgb(int red, int green, int blue, float alpha = 1f) =>
        new(red / 255f, green / 255f, blue / 255f, alpha);

    /// <summary>Hue in degrees around the color wheel, with saturation and lightness from 0 to 1.</summary>
    public static Color Hsl(float hue, float saturation, float lightness, float alpha = 1f) => new(
        HslChannel(0f, hue, saturation, lightness),
        HslChannel(8f, hue, saturation, lightness),
        HslChannel(4f, hue, saturation, lightness),
        alpha);

    /// <summary>
    /// Lightness from 0 to 1, chroma from 0 to about 0.4, and hue in degrees. Colors with
    /// the same lightness look equally bright, which makes this the one to reach for when
    /// building palettes. A color too vivid for a screen is clipped to the nearest channel values.
    /// </summary>
    public static Color Oklch(float lightness, float chroma, float hue, float alpha = 1f)
    {
        float radians = hue * MathF.PI / 180f;
        float a = chroma * MathF.Cos(radians);
        float b = chroma * MathF.Sin(radians);

        float l = Cube(lightness + 0.3963377774f * a + 0.2158037573f * b);
        float m = Cube(lightness - 0.1055613458f * a - 0.0638541728f * b);
        float s = Cube(lightness - 0.0894841775f * a - 1.2914855480f * b);

        return new Color(
            Encode(4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s),
            Encode(-1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s),
            Encode(-0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s),
            alpha);
    }

    private static float HslChannel(float offset, float hue, float saturation, float lightness)
    {
        float position = ((offset + hue / 30f) % 12f + 12f) % 12f;
        float reach = saturation * MathF.Min(lightness, 1f - lightness);
        return lightness - reach * Math.Clamp(MathF.Min(position - 3f, 9f - position), -1f, 1f);
    }

    private static float Cube(float value) => value * value * value;

    /// <summary>Turns a linear channel into the sRGB a screen expects.</summary>
    private static float Encode(float linear)
    {
        float clipped = Math.Clamp(linear, 0f, 1f);
        return clipped <= 0.0031308f ? clipped * 12.92f : 1.055f * MathF.Pow(clipped, 1f / 2.4f) - 0.055f;
    }
}
