namespace Super.Materials;

/// <summary>How a blend combines the base <c>a</c> with the top <c>b</c>. None of them clamp.</summary>
public enum BlendMode
{
    /// <summary>The top.</summary>
    Copy,

    /// <summary><c>a + b</c></summary>
    Add,

    /// <summary><c>a - b</c></summary>
    Subtract,

    /// <summary><c>a * b</c></summary>
    Multiply,

    /// <summary>The smaller of the two.</summary>
    Min,

    /// <summary>The larger of the two.</summary>
    Max,

    /// <summary>Multiplies where the base is dark and screens where it is bright.</summary>
    Overlay,

    /// <summary><c>1 - (1 - a)(1 - b)</c></summary>
    Screen
}

/// <summary>The two images a height blend makes: the combined height and where the top won.</summary>
public sealed record HeightBlendResult(Gray Height, Gray Mask);

public sealed partial class Gray
{
    /// <summary>
    /// Mixes <paramref name="top"/> over this image with <paramref name="mode"/>. The result is
    /// <c>lerp(this, mode(this, top), mask * opacity)</c>, and is not clamped.
    /// </summary>
    public Gray Blend(Gray top, BlendMode mode = BlendMode.Copy, Gray? mask = null, float opacity = 1f)
    {
        return new Gray(new BlendNode(1, Node, top.Node, mask?.Node, mode, opacity));
    }

    /// <summary>Adds <paramref name="other"/>. Shorthand for <see cref="Blend"/> with <see cref="BlendMode.Add"/>.</summary>
    public Gray Add(Gray other) => Blend(other, BlendMode.Add);

    /// <summary>Adds a number to every pixel.</summary>
    public Gray Add(float value) => Blend(Of(value), BlendMode.Add);

    /// <summary>Subtracts <paramref name="other"/>.</summary>
    public Gray Subtract(Gray other) => Blend(other, BlendMode.Subtract);

    /// <summary>Subtracts a number from every pixel.</summary>
    public Gray Subtract(float value) => Blend(Of(value), BlendMode.Subtract);

    /// <summary>Multiplies by <paramref name="other"/>.</summary>
    public Gray Multiply(Gray other) => Blend(other, BlendMode.Multiply);

    /// <summary>Multiplies every pixel by a number.</summary>
    public Gray Multiply(float value) => Blend(Of(value), BlendMode.Multiply);

    /// <summary>The smaller of this and <paramref name="other"/>, pixel by pixel.</summary>
    public Gray Min(Gray other) => Blend(other, BlendMode.Min);

    /// <summary>The smaller of each pixel and a number.</summary>
    public Gray Min(float value) => Blend(Of(value), BlendMode.Min);

    /// <summary>The larger of this and <paramref name="other"/>, pixel by pixel.</summary>
    public Gray Max(Gray other) => Blend(other, BlendMode.Max);

    /// <summary>The larger of each pixel and a number.</summary>
    public Gray Max(float value) => Blend(Of(value), BlendMode.Max);

    /// <summary>
    /// Lays one height map over another so the higher one wins, with a soft seam. Raising
    /// <paramref name="offset"/> above 0.5 lifts the top; a higher <paramref name="contrast"/> makes the seam sharper.
    /// The mask is 1 where the top won.
    /// </summary>
    public static HeightBlendResult HeightBlend(Gray bottom, Gray top, float offset = 0.5f, float contrast = 0.9f)
    {
        return new HeightBlendResult(
            new Gray(new HeightBlendNode(bottom.Node, top.Node, offset, contrast, false)),
            new Gray(new HeightBlendNode(bottom.Node, top.Node, offset, contrast, true)));
    }
}

public sealed partial class Rgb
{
    /// <summary>
    /// Mixes <paramref name="top"/> over this image with <paramref name="mode"/>, channel by channel. The one-channel
    /// <paramref name="mask"/> covers all three. The result is not clamped.
    /// </summary>
    public Rgb Blend(Rgb top, BlendMode mode = BlendMode.Copy, Gray? mask = null, float opacity = 1f)
    {
        return new Rgb(new BlendNode(3, Node, top.Node, mask?.Node, mode, opacity));
    }

    /// <summary>Multiplies every channel by a grey image.</summary>
    public Rgb Multiply(Gray other)
    {
        return new Rgb(new BlendNode(3, Node, other.Node, null, BlendMode.Multiply, 1f));
    }

    /// <summary>Multiplies channel by channel.</summary>
    public Rgb Multiply(Rgb other) => Blend(other, BlendMode.Multiply);
}

/// <summary>Blends a top of one or <paramref name="channels"/> channels over a base. A one-channel top or mask is broadcast.</summary>
sealed class BlendNode(int width, Node baseImage, Node top, Node? mask, BlendMode mode, float opacity)
    : Node(width, mask is null ? [baseImage, top] : [baseImage, top, mask])
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var topChannels = top.Channels;
        var hasMask = mask is not null;

        context.Rows(y =>
        {
            for (var x = 0; x < context.Size; x++)
            {
                var pixel = y * context.Size + x;
                var amount = hasMask ? inputs[2][pixel] * opacity : opacity;

                for (var c = 0; c < Channels; c++)
                {
                    var a = inputs[0][pixel * Channels + c];
                    var b = inputs[1][pixel * topChannels + (topChannels == 1 ? 0 : c)];

                    output[pixel * Channels + c] = a + (Combine(a, b) - a) * amount;
                }
            }
        });
    }

    float Combine(float a, float b)
    {
        switch (mode)
        {
            case BlendMode.Copy:
                return b;
            case BlendMode.Add:
                return a + b;
            case BlendMode.Subtract:
                return a - b;
            case BlendMode.Multiply:
                return a * b;
            case BlendMode.Min:
                return MathF.Min(a, b);
            case BlendMode.Max:
                return MathF.Max(a, b);
            case BlendMode.Overlay:
                return a < 0.5f ? 2f * a * b : 1f - 2f * (1f - a) * (1f - b);
            case BlendMode.Screen:
                return 1f - (1f - a) * (1f - b);
            default:
                throw new InvalidOperationException($"Unknown blend mode {mode}.");
        }
    }
}

/// <summary>Works out either the combined height or the mask of a height blend. Both share the same maths.</summary>
sealed class HeightBlendNode(Node bottom, Node top, float offset, float contrast, bool wantMask) : Node(1, bottom, top)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var width = MathF.Max(1f - contrast, 0.001f);

        context.Rows(y =>
        {
            var start = y * context.Size;

            for (var i = start; i < start + context.Size; i++)
            {
                var low = inputs[0][i];
                var high = inputs[1][i] + (offset - 0.5f);
                var mask = Math.Clamp((high - low) / width + 0.5f, 0f, 1f);

                output[i] = wantMask ? mask : low + (high - low) * mask;
            }
        });
    }
}
