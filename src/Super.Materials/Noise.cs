namespace Super.Materials;

/// <summary>What a <see cref="Noise.Cells"/> image shows.</summary>
public enum CellOutput
{
    /// <summary>The distance to the nearest point.</summary>
    F1,

    /// <summary>The distance to the second nearest point.</summary>
    F2,

    /// <summary>The second distance minus the first: 0 on the borders between cells.</summary>
    Edge,

    /// <summary>A random value per cell.</summary>
    Id,
}

/// <summary>Noise generators. Every one tiles.</summary>
public static class Noise
{
    /// <summary>
    /// Gradient noise from 0 to 1. Octave <c>o</c> has a wrapping lattice of <c>scale * 2^o</c> cells,
    /// and each octave counts <paramref name="persistence"/> times as much as the one before.
    /// </summary>
    public static Gray Perlin(int scale, int octaves = 1, float persistence = 0.5f, int? seed = null)
    {
        Arguments.AtLeast(scale, 1, nameof(scale));
        Arguments.Between(octaves, 1, 12, nameof(octaves));

        return new Gray(new PerlinNode(scale, octaves, persistence, seed));
    }

    /// <summary>
    /// Cellular noise: one point in each of <c>scale x scale</c> cells. Distances are in cell widths, up to 1.
    /// <paramref name="jitter"/> is how far a point strays from the middle of its cell, 0 to 1.
    /// </summary>
    public static Gray Cells(int scale, CellOutput output = CellOutput.F1, float jitter = 1f, int? seed = null)
    {
        Arguments.AtLeast(scale, 1, nameof(scale));
        Arguments.Between(jitter, 0f, 1f, nameof(jitter));

        return new Gray(new CellsNode(scale, output, jitter, seed));
    }

    /// <summary>One random value from 0 to 1 per pixel.</summary>
    public static Gray White(int? seed = null) => new(new WhiteNode(seed));
}

sealed class PerlinNode : Node
{
    // Above this many lattice points per side the gradients are worked out per pixel instead of stored.
    const int TableLimit = 512;

    readonly int _scale;
    readonly int _octaves;
    readonly float _persistence;

    public PerlinNode(int scale, int octaves, float persistence, int? seed) : base(1)
    {
        _scale = scale;
        _octaves = octaves;
        _persistence = persistence;

        if (seed is { } s)
        {
            UseSeed(s);
        }
    }

    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var octaves = new Octave[_octaves];
        var amplitude = 1f;
        var total = 0f;

        for (var o = 0; o < _octaves; o++)
        {
            var cells = _scale << o;
            var seed = Seed + (ulong)o * 0x9E3779B97F4A7C15UL;

            octaves[o] = new Octave(cells, seed, amplitude, cells <= TableLimit ? Gradients(cells, seed) : null);
            total += amplitude * amplitude;
            amplitude *= _persistence;
        }

        // The octaves are independent, so their spread adds as the root of the sum of squares. Dividing by that keeps
        // the spread the same however many octaves there are. 0.8 puts about 3 standard deviations across 0..1.
        var scale = 0.8f / MathF.Sqrt(total);

        context.Rows(y =>
        {
            var v = (y + 0.5f) / size;

            for (var x = 0; x < size; x++)
            {
                var u = (x + 0.5f) / size;
                var sum = 0f;

                foreach (var octave in octaves)
                {
                    sum += octave.Amplitude * Sample(octave, u, v);
                }

                output[y * size + x] = Math.Clamp(0.5f + sum * scale, 0f, 1f);
            }
        });
    }

    static (float, float)[] Gradients(int cells, ulong seed)
    {
        var table = new (float, float)[cells * cells];

        for (var j = 0; j < cells; j++)
        {
            for (var i = 0; i < cells; i++)
            {
                table[j * cells + i] = Gradient(seed, i, j);
            }
        }

        return table;
    }

    static (float, float) Gradient(ulong seed, int i, int j)
    {
        var angle = Hash.Unit(seed, i, j) * (2f * MathF.PI);

        return (DMath.Cos(angle), DMath.Sin(angle));
    }

    static float Sample(Octave octave, float u, float v)
    {
        var cells = octave.Cells;
        var px = u * cells;
        var py = v * cells;
        var fx0 = MathF.Floor(px);
        var fy0 = MathF.Floor(py);
        var x0 = (int)fx0;
        var y0 = (int)fy0;
        var x1 = x0 + 1 == cells ? 0 : x0 + 1;
        var y1 = y0 + 1 == cells ? 0 : y0 + 1;
        var fx = px - fx0;
        var fy = py - fy0;

        var n00 = Corner(octave, x0, y0, fx, fy);
        var n10 = Corner(octave, x1, y0, fx - 1f, fy);
        var n01 = Corner(octave, x0, y1, fx, fy - 1f);
        var n11 = Corner(octave, x1, y1, fx - 1f, fy - 1f);

        var sx = Fade(fx);
        var sy = Fade(fy);
        var top = n00 + sx * (n10 - n00);
        var bottom = n01 + sx * (n11 - n01);

        return top + sy * (bottom - top);
    }

    static float Corner(Octave octave, int i, int j, float dx, float dy)
    {
        var (gx, gy) = octave.Table is { } table ? table[j * octave.Cells + i] : Gradient(octave.Seed, i, j);

        return gx * dx + gy * dy;
    }

    static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    readonly record struct Octave(int Cells, ulong Seed, float Amplitude, (float, float)[]? Table);
}

sealed class CellsNode : Node
{
    readonly int _scale;
    readonly CellOutput _output;
    readonly float _jitter;

    public CellsNode(int scale, CellOutput output, float jitter, int? seed) : base(1)
    {
        _scale = scale;
        _output = output;
        _jitter = jitter;

        if (seed is { } s)
        {
            UseSeed(s);
        }
    }

    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;
        var seedY = Seed + 0xD1B54A32D192ED03UL;
        var seedId = Seed + 0x8CB92BA72F3D8DD7UL;

        context.Rows(y =>
        {
            var py = (y + 0.5f) / size * _scale;
            var cy = (int)MathF.Floor(py);

            for (var x = 0; x < size; x++)
            {
                var px = (x + 0.5f) / size * _scale;
                var cx = (int)MathF.Floor(px);
                var f1 = float.MaxValue;
                var f2 = float.MaxValue;
                var id = 0f;

                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var nx = cx + dx;
                        var ny = cy + dy;
                        var wx = ((nx % _scale) + _scale) % _scale;
                        var wy = ((ny % _scale) + _scale) % _scale;
                        var pointX = nx + 0.5f + (Hash.Unit(Seed, wx, wy) - 0.5f) * _jitter;
                        var pointY = ny + 0.5f + (Hash.Unit(seedY, wx, wy) - 0.5f) * _jitter;
                        var ddx = pointX - px;
                        var ddy = pointY - py;
                        var distance = MathF.Sqrt(ddx * ddx + ddy * ddy);

                        if (distance < f1)
                        {
                            f2 = f1;
                            f1 = distance;
                            id = Hash.Unit(seedId, wx, wy);
                        }
                        else if (distance < f2)
                        {
                            f2 = distance;
                        }
                    }
                }

                var value = _output switch
                {
                    CellOutput.F1 => f1,
                    CellOutput.F2 => f2,
                    CellOutput.Edge => f2 - f1,
                    _ => id,
                };

                output[y * size + x] = Math.Min(value, 1f);
            }
        });
    }
}

sealed class WhiteNode : Node
{
    public WhiteNode(int? seed) : base(1)
    {
        if (seed is { } s)
        {
            UseSeed(s);
        }
    }

    internal override void Evaluate(EvaluationContext context, float[][] inputs, float[] output)
    {
        var size = context.Size;

        context.Rows(y =>
        {
            for (var x = 0; x < size; x++)
            {
                output[y * size + x] = Hash.Unit(Seed, x, y);
            }
        });
    }
}
