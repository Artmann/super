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
