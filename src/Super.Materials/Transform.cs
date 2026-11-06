using System.Numerics;

namespace Super.Materials;

public sealed partial class Gray
{
    /// <summary>
    /// Moves, turns and repeats the image. It is shrunk to 1/<paramref name="tile"/> and repeated that many times
    /// across, scaled by <paramref name="scale"/>, turned <paramref name="rotate"/> degrees counter-clockwise about its
    /// middle, then moved by <paramref name="offset"/> (fractions of the image, right and down). So that it tiles,
    /// <c>tile / scale</c> must be a whole number and the rotation snaps like <see cref="Noise.Directional"/>. A
    /// direction of run a and rise b also shrinks the image by √(a² + b²), so a turned image shows a² + b² copies.
    /// </summary>
    public Gray Transform(Vector2 offset = default, float rotate = 0f, float scale = 1f, int tile = 1)
    {
        return new Gray(TransformNode.Make(Node, 1, offset, rotate, scale, tile));
    }

    /// <summary>
    /// Stamps this image, shrunk to between <paramref name="sizeMin"/> and <paramref name="sizeMax"/> of the image
    /// wide, at <paramref name="count"/> random places over a black image, each combined with <paramref name="mode"/>
    /// in turn. With <paramref name="rotate"/>, each stamp is turned by a random angle. Stamps that cross an edge
    /// wrap. Sizes are more than 0 and at most 0.5.
    /// </summary>
    public Gray Splatter(int count, float sizeMin = 0.05f, float sizeMax = 0.1f, bool rotate = true, BlendMode mode = BlendMode.Max, int? seed = null)
    {
        Arguments.AtLeast(count, 1, nameof(count));
        Arguments.MoreThanUpTo(sizeMin, 0f, SplatterNode.LargestSize, nameof(sizeMin));
        Arguments.Between(sizeMax, sizeMin, SplatterNode.LargestSize, nameof(sizeMax));

        return new Gray(new SplatterNode(Node, count, sizeMin, sizeMax, rotate, mode, seed));
    }
}

public sealed partial class Rgb
{
    /// <summary>The same as <see cref="Gray.Transform"/>, for each channel.</summary>
    public Rgb Transform(Vector2 offset = default, float rotate = 0f, float scale = 1f, int tile = 1)
    {
        return new Rgb(TransformNode.Make(Node, 3, offset, rotate, scale, tile));
    }
}

/// <summary>A direction whose run and rise are whole numbers, so a pattern laid along it tiles.</summary>
internal readonly record struct Direction(int Run, int Rise, float Degrees)
{
    /// <summary>√(run² + rise²): how many periods of a pattern along this direction fit in one image width.</summary>
    public float Length => MathF.Sqrt(Run * Run + Rise * Rise);
}

/// <summary>
/// The angles a tiling pattern can take: every direction (run, rise) with whole numbers from -<see cref="Largest"/> to
/// <see cref="Largest"/>, rise pointing up the image. An angle snaps to the nearest one, and a tie goes to the smaller
/// angle, measured from 0 up to 360.
/// </summary>
internal static class Directions
{
    public const int Largest = 4;

    // Sorted by angle, from 0 up to 360, each direction once in its shortest form.
    static readonly Direction[] All = Build();

    public static Direction Snap(float degrees, string name = "angle")
    {
        Arguments.Finite(degrees, name);

        var angle = degrees % 360f;

        if (angle < 0f)
        {
            angle += 360f;
        }

        if (angle >= 360f)
        {
            angle -= 360f;
        }

        var best = All[0];
        var bestDistance = float.MaxValue;

        foreach (var direction in All)
        {
            var distance = MathF.Abs(angle - direction.Degrees);

            distance = MathF.Min(distance, 360f - distance);

            // Strictly closer, so a tie keeps the earlier, smaller angle.
            if (distance < bestDistance)
            {
                best = direction;
                bestDistance = distance;
            }
        }

        return best;
    }

    static Direction[] Build()
    {
        var all = new List<Direction>();

        for (var rise = -Largest; rise <= Largest; rise++)
        {
            for (var run = -Largest; run <= Largest; run++)
            {
                if (Divisor(Math.Abs(run), Math.Abs(rise)) != 1)
                {
                    continue;
                }

                var degrees = DMath.Atan2(rise, run) * (180f / MathF.PI);

                if (degrees < 0f)
                {
                    degrees += 360f;
                }

                all.Add(new Direction(run, rise, degrees));
            }
        }

        all.Sort((a, b) => a.Degrees.CompareTo(b.Degrees));

        return [.. all];
    }

    // The greatest common divisor. 0 for (0, 0), which is no direction.
    static int Divisor(int a, int b)
    {
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }

        return a;
    }
}

sealed class TransformNode : Node
{
    readonly Vector2 _offset;
    readonly Direction _direction;
    readonly int _copies;

    TransformNode(Node input, int channels, Vector2 offset, Direction direction, int copies) : base(channels, input)
    {
        _offset = offset;
        _direction = direction;
        _copies = copies;
    }

    public static TransformNode Make(Node input, int channels, Vector2 offset, float rotate, float scale, int tile)
    {
        Arguments.Finite(offset.X, nameof(offset));
        Arguments.Finite(offset.Y, nameof(offset));
        Arguments.MoreThan(scale, 0f, nameof(scale));
        Arguments.AtLeast(tile, 1, nameof(tile));

        var copies = tile / scale;
        var whole = MathF.Round(copies);

        if (whole < 1f || MathF.Abs(copies - whole) > 1e-4f * whole)
        {
            throw new ArgumentException(FormattableString.Invariant($"tile / scale must be a whole number so the image tiles, not {tile} / {scale} = {copies}. Use a scale of 1, 0.5, 0.25 and so on, or one that divides tile."));
        }

        return new TransformNode(input, channels, offset, Directions.Snap(rotate, nameof(rotate)), (int)whole);
    }

    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var source = inputs[0];
        var a = (float)_direction.Run;
        var b = (float)_direction.Rise;
        var k = (float)_copies;

        context.Rows(y =>
        {
            var dy = (y + 0.5f) / size - _offset.Y - 0.5f;

            for (var x = 0; x < size; x++)
            {
                var dx = (x + 0.5f) / size - _offset.X - 0.5f;

                // Where this pixel reads from: turned back about the middle by a whole-number matrix, so a step of one
                // image is a whole number of steps in the source, then repeated k times.
                var u = k * ((a * dx - b * dy) + 0.5f);
                var v = k * ((b * dx + a * dy) + 0.5f);
                var at = (y * size + x) * Channels;

                for (var c = 0; c < Channels; c++)
                {
                    output[at + c] = Sampling.Bilinear(source, size, Channels, c, u, v);
                }
            }
        });
    }
}

sealed class SplatterNode : Node
{
    /// <summary>The widest a stamp can be, so that a turned stamp never wraps onto itself.</summary>
    public const float LargestSize = 0.5f;

    readonly int _count;
    readonly float _sizeMin;
    readonly float _sizeMax;
    readonly bool _rotate;
    readonly BlendMode _mode;

    public SplatterNode(Node input, int count, float sizeMin, float sizeMax, bool rotate, BlendMode mode, int? seed) : base(1, input)
    {
        _count = count;
        _sizeMin = sizeMin;
        _sizeMax = sizeMax;
        _rotate = rotate;
        _mode = mode;

        if (seed is { } s)
        {
            UseSeed(s);
        }
    }

    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var source = inputs[0];
        var stamps = new Stamp[_count];

        for (var i = 0; i < _count; i++)
        {
            var width = _sizeMin + (_sizeMax - _sizeMin) * Hash.Unit(Seed, i, 2);
            var angle = _rotate ? Hash.Unit(Seed, i, 3) * (2f * MathF.PI) : 0f;
            var cos = _rotate ? DMath.Cos(angle) : 1f;
            var sin = _rotate ? DMath.Sin(angle) : 0f;

            // Half the width of the box a turned square fills.
            var reach = width / 2f * (MathF.Abs(cos) + MathF.Abs(sin));

            stamps[i] = new Stamp(Hash.Unit(Seed, i, 0), Hash.Unit(Seed, i, 1), width, cos, sin, reach);
        }

        // Each row goes through the stamps in order, so every pixel combines them in the same order on any thread.
        context.Rows(y =>
        {
            var row = y * size;
            var v = (y + 0.5f) / size;

            Array.Clear(output, row, size);

            foreach (var stamp in stamps)
            {
                var dv = v - stamp.Y;

                dv -= MathF.Round(dv);

                if (MathF.Abs(dv) > stamp.Reach)
                {
                    continue;
                }

                var first = (int)MathF.Floor((stamp.X - stamp.Reach) * size - 0.5f);
                var last = Math.Min((int)MathF.Ceiling((stamp.X + stamp.Reach) * size - 0.5f), first + size - 1);

                for (var i = first; i <= last; i++)
                {
                    var x = Sampling.Wrap(i, size);
                    var du = (x + 0.5f) / size - stamp.X;

                    du -= MathF.Round(du);

                    // Turned back into the stamp, where 0 to 1 is the whole source image.
                    var lu = (stamp.Cos * du + stamp.Sin * dv) / stamp.Width + 0.5f;
                    var lv = (stamp.Cos * dv - stamp.Sin * du) / stamp.Width + 0.5f;

                    if (lu < 0f || lu > 1f || lv < 0f || lv > 1f)
                    {
                        continue;
                    }

                    var value = Sampling.Bilinear(source, size, 1, 0, lu, lv);

                    output[row + x] = BlendNode.Combine(_mode, output[row + x], value);
                }
            }
        });
    }

    readonly record struct Stamp(float X, float Y, float Width, float Cos, float Sin, float Reach);
}
