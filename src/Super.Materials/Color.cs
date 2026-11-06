using System.Globalization;

namespace Super.Materials;

/// <summary>Colour stops along 0 to 1, for turning a grey image into colour with <see cref="Gray.GradientMap"/>.</summary>
public sealed class Ramp
{
    readonly float[] _at;
    readonly (float R, float G, float B)[] _colors;

    Ramp(float[] at, (float R, float G, float B)[] colors)
    {
        _at = at;
        _colors = colors;
    }

    /// <summary>Spreads the colours evenly from 0 to 1. Colours are written #rrggbb.</summary>
    public static Ramp Of(params string[] hex)
    {
        if (hex.Length == 0)
        {
            throw new ArgumentException("A ramp needs at least one colour.", nameof(hex));
        }

        var stops = new (float At, string Hex)[hex.Length];

        for (var i = 0; i < hex.Length; i++)
        {
            stops[i] = (hex.Length == 1 ? 0f : i / (float)(hex.Length - 1), hex[i]);
        }

        return Of(stops);
    }

    /// <summary>Places each colour at a position from 0 to 1. Positions must not go back.</summary>
    public static Ramp Of(params (float At, string Hex)[] stops)
    {
        if (stops.Length == 0)
        {
            throw new ArgumentException("A ramp needs at least one stop.", nameof(stops));
        }

        var at = new float[stops.Length];
        var colors = new (float R, float G, float B)[stops.Length];

        for (var i = 0; i < stops.Length; i++)
        {
            Arguments.Between(stops[i].At, 0f, 1f, "At");

            if (i > 0 && stops[i].At < stops[i - 1].At)
            {
                throw new ArgumentException(string.Create(
                    CultureInfo.InvariantCulture,
                    $"Ramp stops must be in order: {stops[i].At} comes after {stops[i - 1].At}."));
            }

            at[i] = stops[i].At;
            colors[i] = Rgb.ParseHex(stops[i].Hex);
        }

        return new Ramp(at, colors);
    }

    /// <summary>The colour at <paramref name="t"/>, which is clamped to 0..1. Before the first stop and after the last it holds their colour.</summary>
    internal (float R, float G, float B) Sample(float t)
    {
        t = Math.Clamp(t, 0f, 1f);

        if (t <= _at[0])
        {
            return _colors[0];
        }

        for (var i = 1; i < _at.Length; i++)
        {
            if (t <= _at[i])
            {
                var span = _at[i] - _at[i - 1];
                var f = span > 0f ? (t - _at[i - 1]) / span : 1f;
                var (r0, g0, b0) = _colors[i - 1];
                var (r1, g1, b1) = _colors[i];

                return (r0 + (r1 - r0) * f, g0 + (g1 - g0) * f, b0 + (b1 - b0) * f);
            }
        }

        return _colors[^1];
    }
}

public sealed partial class Gray
{
    /// <summary>Colours each pixel from <paramref name="ramp"/>, using the value (clamped to 0..1) as the position.</summary>
    public Rgb GradientMap(Ramp ramp) => new(new GradientMapNode(Node, ramp));
}

sealed class GradientMapNode(Node input, Ramp ramp) : Node(3, input)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        context.Rows(y =>
        {
            for (var x = 0; x < context.Size; x++)
            {
                var pixel = y * context.Size + x;
                var (r, g, b) = ramp.Sample(inputs[0][pixel]);

                output[pixel * 3] = r;
                output[pixel * 3 + 1] = g;
                output[pixel * 3 + 2] = b;
            }
        });
    }
}

public sealed partial class Rgb
{
    /// <summary>
    /// Shifts the colour in HSL. <paramref name="hue"/> turns it, where 1 is a whole turn round the colour wheel (so 0.5
    /// turns red to cyan) and any number is allowed. <paramref name="saturation"/> and <paramref name="lightness"/> are
    /// added, and the results are held to 0..1. The channels are taken as stored, held to 0..1 first.
    /// </summary>
    public Rgb Hsl(float hue = 0f, float saturation = 0f, float lightness = 0f)
    {
        Arguments.Finite(hue, nameof(hue));
        Arguments.Finite(saturation, nameof(saturation));
        Arguments.Finite(lightness, nameof(lightness));

        return new Rgb(new HslNode(Node, hue, saturation, lightness));
    }

    /// <summary>
    /// One grey value from the three channels as stored, with the Rec. 709 weights: 0.2126 red, 0.7152 green and
    /// 0.0722 blue. Nothing is clamped or converted to linear first.
    /// </summary>
    public Gray ToGray() => new(new ToGrayNode(Node));
}

sealed class ToGrayNode(Node input) : Node(1, input)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        context.Rows(y =>
        {
            for (var pixel = y * context.Size; pixel < (y + 1) * context.Size; pixel++)
            {
                var at = pixel * 3;

                output[pixel] = 0.2126f * inputs[0][at] + 0.7152f * inputs[0][at + 1] + 0.0722f * inputs[0][at + 2];
            }
        });
    }
}

sealed class HslNode(Node input, float hue, float saturation, float lightness) : Node(3, input)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        context.Rows(y =>
        {
            for (var pixel = y * context.Size; pixel < (y + 1) * context.Size; pixel++)
            {
                var at = pixel * 3;
                var r = Math.Clamp(inputs[0][at], 0f, 1f);
                var g = Math.Clamp(inputs[0][at + 1], 0f, 1f);
                var b = Math.Clamp(inputs[0][at + 2], 0f, 1f);

                var high = MathF.Max(r, MathF.Max(g, b));
                var low = MathF.Min(r, MathF.Min(g, b));
                var spread = high - low;
                var l = (high + low) / 2f;
                var s = spread == 0f ? 0f : spread / (1f - MathF.Abs(2f * l - 1f));
                var h = 0f;

                if (spread > 0f)
                {
                    if (high == r)
                    {
                        h = (g - b) / spread;

                        if (h < 0f)
                        {
                            h += 6f;
                        }
                    }
                    else if (high == g)
                    {
                        h = (b - r) / spread + 2f;
                    }
                    else
                    {
                        h = (r - g) / spread + 4f;
                    }

                    h /= 6f;
                }

                h += hue;
                h -= MathF.Floor(h);
                s = Math.Clamp(s + saturation, 0f, 1f);
                l = Math.Clamp(l + lightness, 0f, 1f);

                var chroma = (1f - MathF.Abs(2f * l - 1f)) * s;
                var sector = h * 6f;
                var second = chroma * (1f - MathF.Abs(sector - 2f * MathF.Floor(sector / 2f) - 1f));
                var m = l - chroma / 2f;
                var (cr, cg, cb) = (int)MathF.Floor(sector) switch
                {
                    0 => (chroma, second, 0f),
                    1 => (second, chroma, 0f),
                    2 => (0f, chroma, second),
                    3 => (0f, second, chroma),
                    4 => (second, 0f, chroma),
                    _ => (chroma, 0f, second)
                };

                output[at] = cr + m;
                output[at + 1] = cg + m;
                output[at + 2] = cb + m;
            }
        });
    }
}
