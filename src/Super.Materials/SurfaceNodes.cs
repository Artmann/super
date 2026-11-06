namespace Super.Materials;

public sealed partial class Gray
{
    /// <summary>
    /// Turns this height into a normal map with a Sobel filter. A value of 1 is <paramref name="depth"/> metres high,
    /// and the image is <paramref name="width"/> metres across, so a steeper slope tilts the normal more.
    /// </summary>
    public Normals HeightToNormal(float width, float depth)
    {
        Arguments.MoreThan(width, 0f, "Width");
        Arguments.AtLeast(depth, 0f, "Depth");

        return new Normals(new HeightToNormalNode(Node, width, depth));
    }

    /// <summary>
    /// Darkens crevices by looking for the horizon in 8 directions, out to <paramref name="radius"/> of the image.
    /// 1 is open and lower is more hidden. <paramref name="width"/> and <paramref name="depth"/> are in metres, as in <see cref="HeightToNormal"/>.
    /// </summary>
    public Gray HeightToOcclusion(float width, float depth, float radius = 0.05f)
    {
        Arguments.MoreThan(width, 0f, "Width");
        Arguments.AtLeast(depth, 0f, "Depth");
        Arguments.Between(radius, 0f, 1f, nameof(radius));

        return new Gray(new HeightToOcclusionNode(Node, width, depth, radius));
    }
}

/// <summary>Maths on normals as float vectors, so every node that lays one normal over another gives the same answer.</summary>
internal static class NormalMath
{
    /// <summary>
    /// Reoriented normal mapping: lays <paramref name="detail"/> over <paramref name="baseNormal"/>, so a flat detail
    /// returns the base. Both are x, y, z from -1 to 1 and the result has length 1.
    /// </summary>
    public static (float X, float Y, float Z) Reorient((float X, float Y, float Z) baseNormal, (float X, float Y, float Z) detail)
    {
        var t = (X: baseNormal.X, Y: baseNormal.Y, Z: baseNormal.Z + 1f);
        var u = (X: -detail.X, Y: -detail.Y, Z: detail.Z);
        var dot = t.X * u.X + t.Y * u.Y + t.Z * u.Z;
        var scale = dot / t.Z;
        var x = t.X * scale - u.X;
        var y = t.Y * scale - u.Y;
        var z = t.Z * scale - u.Z;
        var length = MathF.Sqrt(x * x + y * y + z * z);

        if (!(length > 0f))
        {
            return (0f, 0f, 1f);
        }

        return (x / length, y / length, z / length);
    }
}

sealed class HeightToNormalNode(Node input, float width, float depth) : Node(3, input)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var height = inputs[0];
        var metresPerPixel = width / size;

        context.Rows(y =>
        {
            var up = Sampling.Wrap(y - 1, size) * size;
            var mid = y * size;
            var down = Sampling.Wrap(y + 1, size) * size;

            for (var x = 0; x < size; x++)
            {
                var left = Sampling.Wrap(x - 1, size);
                var right = Sampling.Wrap(x + 1, size);

                var sx = (height[up + right] + 2f * height[mid + right] + height[down + right]
                    - height[up + left] - 2f * height[mid + left] - height[down + left]) * depth / 8f;

                // The rise towards row 0, which is up the image.
                var sy = (height[up + left] + 2f * height[up + x] + height[up + right]
                    - height[down + left] - 2f * height[down + x] - height[down + right]) * depth / 8f;

                var nx = -sx / metresPerPixel;
                var ny = -sy / metresPerPixel;
                var length = MathF.Sqrt(nx * nx + ny * ny + 1f);
                var o = (mid + x) * 3;

                output[o] = nx / length;
                output[o + 1] = ny / length;
                output[o + 2] = 1f / length;
            }
        });
    }
}

sealed class HeightToOcclusionNode(Node input, float width, float depth, float radius) : Node(1, input)
{
    const int Directions = 8;
    const int Steps = 8;

    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var height = inputs[0];
        var metresPerPixel = width / size;
        var stepPixels = radius * size / Steps;
        var cos = new float[Directions];
        var sin = new float[Directions];

        for (var d = 0; d < Directions; d++)
        {
            var angle = 2f * MathF.PI * d / Directions;

            cos[d] = DMath.Cos(angle);
            sin[d] = DMath.Sin(angle);
        }

        context.Rows(y =>
        {
            for (var x = 0; x < size; x++)
            {
                var h0 = height[y * size + x] * depth;
                var hidden = 0f;

                for (var d = 0; d < Directions; d++)
                {
                    var steepest = 0f;

                    for (var step = 1; step <= Steps; step++)
                    {
                        var reach = step * stepPixels;
                        var ox = (int)MathF.Round(cos[d] * reach);
                        var oy = (int)MathF.Round(sin[d] * reach);

                        if (ox == 0 && oy == 0)
                        {
                            continue;
                        }

                        var distance = MathF.Sqrt(ox * ox + oy * oy) * metresPerPixel;
                        var h = height[Sampling.Wrap(y + oy, size) * size + Sampling.Wrap(x + ox, size)] * depth;
                        var slope = (h - h0) / distance;

                        if (slope > steepest)
                        {
                            steepest = slope;
                        }
                    }

                    // The sine of the angle above the flat, which is how much of that direction's sky is gone.
                    hidden += steepest / MathF.Sqrt(1f + steepest * steepest);
                }

                output[y * size + x] = 1f - hidden / Directions;
            }
        });
    }
}

sealed class NormalOverNode(Node baseNormal, Node detail) : Node(3, baseNormal, detail)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        context.Rows(y =>
        {
            for (var x = 0; x < context.Size; x++)
            {
                var o = (y * context.Size + x) * 3;
                var b = inputs[0];
                var d = inputs[1];
                var (nx, ny, nz) = NormalMath.Reorient((b[o], b[o + 1], b[o + 2]), (d[o], d[o + 1], d[o + 2]));

                output[o] = nx;
                output[o + 1] = ny;
                output[o + 2] = nz;
            }
        });
    }
}

/// <summary>Multiplies a colour by <c>lerp(1, occlusion, strength)</c>.</summary>
sealed class OccludedColorNode(Node color, Node occlusion, float strength) : Node(3, color, occlusion)
{
    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        context.Rows(y =>
        {
            for (var x = 0; x < context.Size; x++)
            {
                var pixel = y * context.Size + x;
                var factor = 1f + (inputs[1][pixel] - 1f) * strength;

                for (var c = 0; c < 3; c++)
                {
                    output[pixel * 3 + c] = inputs[0][pixel * 3 + c] * factor;
                }
            }
        });
    }
}
