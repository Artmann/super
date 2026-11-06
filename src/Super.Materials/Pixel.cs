using System.Numerics;

namespace Super.Materials;

/// <summary>Works out a grey pixel. Called once per pixel, in parallel, so it must not change anything outside it.</summary>
public delegate float PixelGray(PixelArgs p);

/// <summary>Works out a colour pixel (red, green, blue). Called once per pixel, in parallel.</summary>
public delegate Vector3 PixelRgb(PixelArgs p);

/// <summary>What a <see cref="Pixel"/> body can read: where it is, and the inputs at this pixel or anywhere.</summary>
public readonly ref struct PixelArgs
{
    readonly float[][] _inputs;
    readonly int[] _channels;
    readonly int _size;
    readonly int _offset;

    internal PixelArgs(float[][] inputs, int[] channels, int size, int x, int y)
    {
        _inputs = inputs;
        _channels = channels;
        _size = size;
        _offset = y * size + x;
        Uv = new Vector2((x + 0.5f) / size, (y + 0.5f) / size);
    }

    /// <summary>The centre of this pixel as fractions of the image, 0 to 1. Row 0 is the top.</summary>
    public Vector2 Uv { get; }

    /// <summary>Input <paramref name="input"/> at this pixel. It must be a one-channel image.</summary>
    public float Gray(int input)
    {
        Check(input, 1, "Gray", "Rgb");

        return _inputs[input][_offset];
    }

    /// <summary>Input <paramref name="input"/> at this pixel. It must be a three-channel image.</summary>
    public Vector3 Rgb(int input)
    {
        Check(input, 3, "Rgb", "Gray");

        var at = _offset * 3;
        var values = _inputs[input];

        return new Vector3(values[at], values[at + 1], values[at + 2]);
    }

    /// <summary>Input <paramref name="input"/> read bilinearly at <paramref name="uv"/>, as fractions of the image. Wraps at the edges.</summary>
    public float SampleGray(int input, Vector2 uv)
    {
        Check(input, 1, "SampleGray", "SampleRgb");

        return Sampling.Bilinear(_inputs[input], _size, 1, 0, uv.X, uv.Y);
    }

    /// <summary>Input <paramref name="input"/> read bilinearly at <paramref name="uv"/>, as fractions of the image. Wraps at the edges.</summary>
    public Vector3 SampleRgb(int input, Vector2 uv)
    {
        Check(input, 3, "SampleRgb", "SampleGray");

        var values = _inputs[input];

        return new Vector3(
            Sampling.Bilinear(values, _size, 3, 0, uv.X, uv.Y),
            Sampling.Bilinear(values, _size, 3, 1, uv.X, uv.Y),
            Sampling.Bilinear(values, _size, 3, 2, uv.X, uv.Y));
    }

    void Check(int input, int channels, string used, string instead)
    {
        if (input < 0 || input >= _inputs.Length)
        {
            var count = _inputs.Length == 1 ? "1 input" : FormattableString.Invariant($"{_inputs.Length} inputs");

            throw new ArgumentOutOfRangeException(nameof(input), FormattableString.Invariant($"{used}({input}) has no input {input}: the node has {count}."));
        }

        if (_channels[input] != channels)
        {
            var needs = channels == 1 ? "one" : "three";
            var has = _channels[input] == 1 ? "one" : "three";

            throw new InvalidOperationException(FormattableString.Invariant($"{used}({input}) needs a {needs}-channel input, but input {input} has {has}. Use {instead}({input}) for it."));
        }
    }
}

/// <summary>
/// A node you write yourself, as a function of one pixel. Use <see cref="DMath"/> in the body for sine, cosine,
/// powers, logs and atan2, never <c>Math</c> or <c>MathF</c>, so the result is the same on every machine.
/// </summary>
public static class Pixel
{
    /// <summary>A grey image whose every pixel is <paramref name="body"/>. <paramref name="inputs"/> are numbered from 0.</summary>
    public static Gray Gray(PixelGray body, params Image[] inputs)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(inputs);

        return new Gray(new PixelNode(1, inputs, (PixelArgs p, float[] output, int at) => output[at] = body(p)));
    }

    /// <summary>A colour image whose every pixel is <paramref name="body"/>. <paramref name="inputs"/> are numbered from 0.</summary>
    public static Rgb Rgb(PixelRgb body, params Image[] inputs)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(inputs);

        return new Rgb(new PixelNode(3, inputs, (PixelArgs p, float[] output, int at) =>
        {
            var color = body(p);

            output[at] = color.X;
            output[at + 1] = color.Y;
            output[at + 2] = color.Z;
        }));
    }
}

delegate void PixelWriter(PixelArgs p, float[] output, int at);

sealed class PixelNode(int channels, Image[] images, PixelWriter write) : Node(channels, images.Select(i => i.Node).ToArray())
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var kinds = Inputs.Select(i => i.Channels).ToArray();

        context.Rows(y =>
        {
            for (var x = 0; x < size; x++)
            {
                var p = new PixelArgs(inputs, kinds, size, x, y);

                write(p, output, (y * size + x) * Channels);
            }
        });
    }
}
