using System.Globalization;

namespace Super.Materials;

/// <summary>A handle to an image a graph makes. Nothing is worked out until the graph is cooked.</summary>
public abstract class Image
{
    internal Image(Node node)
    {
        Node = node;
    }

    internal Node Node { get; }
}

/// <summary>One channel, usually 0 to 1.</summary>
public sealed partial class Gray : Image
{
    internal Gray(Node node) : base(node)
    {
    }

    public static Gray Of(float value) => new(new ConstantNode([value]));

    internal static Gray FromArray(float[] values) => new(new ArrayNode(values));
}

/// <summary>Three channels, red, green and blue, as sRGB values as authored.</summary>
public sealed partial class Rgb : Image
{
    internal Rgb(Node node) : base(node)
    {
    }

    public static Rgb Of(float r, float g, float b) => new(new ConstantNode([r, g, b]));

    public static Rgb Of(string hex)
    {
        var (r, g, b) = ParseHex(hex);

        return Of(r, g, b);
    }

    /// <summary>Reads #rrggbb straight to 0..1. Makes no node, so a ramp can use it outside a graph.</summary>
    internal static (float R, float G, float B) ParseHex(string hex)
    {
        if (hex.Length != 7 || hex[0] != '#' || !int.TryParse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"Colours are written #rrggbb, not {hex}.");
        }

        return (((value >> 16) & 0xff) / 255f, ((value >> 8) & 0xff) / 255f, (value & 0xff) / 255f);
    }
}

/// <summary>A direction per pixel: x, y and z from -1 to 1.</summary>
public sealed partial class Normals : Image
{
    internal Normals(Node node) : base(node)
    {
    }
}

sealed class ConstantNode(float[] values) : Node(values.Length)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        context.Rows(y =>
        {
            var start = y * context.Size * Channels;

            for (var x = 0; x < context.Size; x++)
            {
                for (var c = 0; c < Channels; c++)
                {
                    output[start + x * Channels + c] = values[c];
                }
            }
        });
    }
}

sealed class ArrayNode(float[] values) : Node(1)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var expected = context.Size * context.Size;

        if (values.Length != expected)
        {
            throw new InvalidOperationException($"Expected {expected} values for a {context.Size} x {context.Size} image, got {values.Length}.");
        }

        Array.Copy(values, output, expected);
    }
}
