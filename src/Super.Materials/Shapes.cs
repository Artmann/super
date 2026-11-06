namespace Super.Materials;

/// <summary>What <see cref="Shape.Of"/> draws.</summary>
public enum ShapeKind
{
    /// <summary>1 inside a circle, 0 outside.</summary>
    Disc,

    /// <summary>1 inside a square, 0 outside.</summary>
    Square,

    /// <summary>Inside a square, a ramp from 0 at its left edge to 1 at its right. 0 outside.</summary>
    Gradient,

    /// <summary><c>0.5 + 0.5 cos(π r)</c> for the distance r from the middle, in half widths. 0 from r = 1 out.</summary>
    Bell,

    /// <summary>1 at the middle, falling straight to 0 at the edges of a square.</summary>
    Pyramid,
}

/// <summary>One shape in the middle of the image.</summary>
public static class Shape
{
    /// <summary>A <paramref name="kind"/> of shape, centred, <paramref name="size"/> of the image wide: more than 0, at most 1.</summary>
    public static Gray Of(ShapeKind kind, float size = 1f)
    {
        Arguments.MoreThanUpTo(size, 0f, 1f, nameof(size));

        return new Gray(new ShapeNode(kind, size));
    }
}

/// <summary>Gradients that tile.</summary>
public static class Gradient
{
    /// <summary>
    /// Rises from 0 to 1 over half the image along <paramref name="angle"/> (degrees, counter-clockwise from +x) and
    /// falls back over the other half, so it tiles. The angle snaps like <see cref="Noise.Directional"/>, and along a
    /// direction of run a and rise b the ramp repeats every 1/√(a² + b²) of the image.
    /// </summary>
    public static Gray Linear(float angle = 0f)
    {
        return new Gray(new LinearGradientNode(Directions.Snap(angle, nameof(angle))));
    }

    /// <summary>1 at the middle, falling with the distance to 0 at the corners.</summary>
    public static Gray Radial() => new(new RadialGradientNode());
}

sealed class ShapeNode(ShapeKind kind, float size) : Node(1)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var count = context.Size;
        var half = size / 2f;

        context.Rows(y =>
        {
            var dy = ((y + 0.5f) / count - 0.5f) / half;

            for (var x = 0; x < count; x++)
            {
                var dx = ((x + 0.5f) / count - 0.5f) / half;

                output[y * count + x] = Value(dx, dy);
            }
        });
    }

    // dx and dy are from the middle, in half widths, so the shape's edge is at 1.
    float Value(float dx, float dy)
    {
        var box = MathF.Max(MathF.Abs(dx), MathF.Abs(dy));

        switch (kind)
        {
            case ShapeKind.Disc:
                return dx * dx + dy * dy <= 1f ? 1f : 0f;
            case ShapeKind.Square:
                return box <= 1f ? 1f : 0f;
            case ShapeKind.Gradient:
                return box <= 1f ? (dx + 1f) / 2f : 0f;
            case ShapeKind.Bell:
            {
                var r = MathF.Sqrt(dx * dx + dy * dy);

                return r < 1f ? 0.5f + 0.5f * DMath.Cos(MathF.PI * r) : 0f;
            }
            case ShapeKind.Pyramid:
                return MathF.Max(1f - box, 0f);
            default:
                throw new InvalidOperationException($"Unknown shape {kind}.");
        }
    }
}

sealed class LinearGradientNode(Direction direction) : Node(1)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var a = direction.Run;
        var b = direction.Rise;

        context.Rows(y =>
        {
            var v = (y + 0.5f) / size;

            for (var x = 0; x < size; x++)
            {
                var u = (x + 0.5f) / size;

                // How far along the direction, in periods. v points down the image and rise points up.
                var s = a * u - b * v;

                s -= MathF.Floor(s);
                output[y * size + x] = 1f - MathF.Abs(2f * s - 1f);
            }
        });
    }
}

sealed class RadialGradientNode() : Node(1)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;

        context.Rows(y =>
        {
            var dy = (y + 0.5f) / size - 0.5f;

            for (var x = 0; x < size; x++)
            {
                var dx = (x + 0.5f) / size - 0.5f;

                // The corners are √0.5 from the middle, so dividing by that is the root of twice the squared distance.
                output[y * size + x] = MathF.Max(1f - MathF.Sqrt(2f * (dx * dx + dy * dy)), 0f);
            }
        });
    }
}
