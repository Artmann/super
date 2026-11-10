using System.Numerics;
using Super.Materials;

/// <summary>
/// An old cobweb in a corner, 0.5 m across, for a triangular card whose right angle is the image's top-left corner.
/// Two anchor threads run along the corner's sides, about 7 mm in, and a frame thread runs across from one to the
/// other. Inside the frame an orb web of 18 spokes holds a spiral that is broken in many places, and a tangle of loose
/// threads crosses it, with dust caught where they gather. The threads are about a millimetre thick, much thicker than
/// a spider's, so they still show a few metres away. Everything but the threads has an opacity of 0, so a rule's
/// <c>alphaCutoff</c> cuts it out.
/// </summary>
public sealed class Cobweb : MaterialGraph
{
    /// <summary>The middle of the orb, in fractions of the image, row 0 at the top.</summary>
    public static readonly Vector2 Hub = new(0.3f, 0.28f);

    /// <summary>How far in from the corner's sides the anchor threads run.</summary>
    public const float Inset = 0.015f;

    /// <summary>Where the frame thread meets the top side and the left side.</summary>
    public static readonly Vector2 FrameTop = new(0.9f, Inset);

    /// <inheritdoc cref="FrameTop"/>
    public static readonly Vector2 FrameLeft = new(Inset, 0.86f);

    private const int Spokes = 18;
    private const int Strays = 12;

    // Half a thread's thickness, and how soft its edges are, in fractions of the image.
    private const float Thread = 0.0009f;
    private const float Soft = 0.0006f;

    public override Material Build()
    {
        int seed = Seed;
        var spokes = SpokeAngles(seed);
        var strays = StrayThreads(seed);

        // How much of the web is torn away: the spiral survives in patches.
        var torn = Noise.Perlin(scale: 6, octaves: 2, seed: 2);
        var distance = Pixel.Gray(p => Distance(p.Uv, spokes, strays, p.Gray(0), seed), torn);
        var threads = distance.Levels(Thread - Soft, Thread + Soft).Invert();

        // Dust caught on the threads: soft clumps along them, more of it near the corner.
        var clumps = Noise.White(seed: 3).Blur(0.002f).Levels(0.52f, 0.56f);
        var nearCorner = Pixel.Gray(p => 1f - Math.Clamp((p.Uv.X + p.Uv.Y) / 0.8f, 0f, 1f));
        var dust = distance.Levels(0f, 0.004f).Invert().Multiply(clumps.Max(nearCorner.Multiply(0.5f)))
            .Multiply(Noise.Perlin(scale: 10, octaves: 2, seed: 4).Levels(0.4f, 0.7f));
        var opacity = threads.Max(dust.Levels(0.3f, 0.5f));

        // Pale silk greyed by dust, darker where the dust clumps.
        var silk = Noise.Perlin(scale: 20, octaves: 3, seed: 5).GradientMap(Ramp.Of("#a9a69c", "#c4c1b6", "#b5b1a5"));
        var color = silk.Blend(Rgb.Of("#7d776b"), mask: dust, opacity: 0.8f);

        return new Material
        {
            Width = 0.5f,
            Depth = 0.0005f,
            Occlusion = 0.2f,
            Height = opacity.Multiply(0.8f),
            Color = color,
            // Silk has a slight sheen; dust is dull.
            Roughness = dust.Remap(0.45f, 0.9f).Add(Noise.Perlin(scale: 16, octaves: 2, seed: 6).Remap(-0.1f, 0.1f)),
            Opacity = opacity,
        };
    }

    // The spokes' angles round the hub, roughly even.
    private static float[] SpokeAngles(int seed)
    {
        var angles = new float[Spokes];
        for (int i = 0; i < Spokes; i++)
        {
            angles[i] = (i + 0.35f * (Unit(seed, i) - 0.5f)) * (2f * MathF.PI / Spokes);
        }

        return angles;
    }

    // Loose threads: each from a point on the anchor or frame threads to another inside the web.
    private static (Vector2 From, Vector2 To)[] StrayThreads(int seed)
    {
        var threads = new (Vector2, Vector2)[Strays];
        for (int i = 0; i < Strays; i++)
        {
            float t = Unit(seed, 100 + i * 4);
            var from = (i % 3) switch
            {
                0 => new Vector2(Inset + t * (FrameTop.X - Inset), Inset),
                1 => new Vector2(Inset, Inset + t * (FrameLeft.Y - Inset)),
                _ => Vector2.Lerp(FrameTop, FrameLeft, t),
            };
            var to = new Vector2(0.03f + 0.5f * Unit(seed, 101 + i * 4), 0.03f + 0.5f * Unit(seed, 102 + i * 4));
            threads[i] = (from, to);
        }

        return threads;
    }

    // How far a point is from the nearest thread, in fractions of the image.
    private static float Distance(Vector2 uv, float[] spokes, (Vector2 From, Vector2 To)[] strays, float torn, int seed)
    {
        // Beyond the frame there is nothing.
        if (Beyond(uv) > 0f)
        {
            return 1f;
        }

        // The anchor threads along the corner's sides, and the frame thread across.
        float nearest = MathF.Min(MathF.Abs(uv.Y - Inset), MathF.Abs(uv.X - Inset));
        nearest = MathF.Min(nearest, Segment(uv, FrameTop, FrameLeft));

        foreach (var (from, to) in strays)
        {
            nearest = MathF.Min(nearest, Segment(uv, from, to));
        }

        // The spokes, from the hub out to the frame.
        var offset = uv - Hub;
        foreach (float angle in spokes)
        {
            var direction = new Vector2(DMath.Cos(angle), DMath.Sin(angle));
            nearest = MathF.Min(nearest, Segment(uv, Hub, Hub + direction * Reach(direction)));
        }

        // The spiral: between two spokes it runs straight from one to the next, a little further out each turn.
        float turn = DMath.Atan2(offset.Y, offset.X);
        if (turn < spokes[0])
        {
            turn += 2f * MathF.PI;
        }

        int sector = Spokes - 1;
        for (int i = 0; i < Spokes - 1; i++)
        {
            if (turn < spokes[i + 1])
            {
                sector = i;
                break;
            }
        }

        float start = spokes[sector];
        float end = sector + 1 < Spokes ? spokes[sector + 1] : spokes[0] + 2f * MathF.PI;
        float middle = (start + end) * 0.5f;
        float chord = DMath.Cos(turn - middle) / DMath.Cos((end - start) * 0.5f);
        float radius = offset.Length() * chord;
        const float Gap = 0.017f;
        float wound = (radius - 0.035f) / Gap - (turn - spokes[0]) / (2f * MathF.PI);
        float ring = MathF.Round(wound);
        float limit = 0.82f * MathF.Min(Reach(new Vector2(DMath.Cos(start), DMath.Sin(start))), Reach(new Vector2(DMath.Cos(end), DMath.Sin(end))));

        // A stretch of spiral is kept where the web isn't torn, and broken off at random elsewhere.
        bool kept = Unit(seed, 1000 + (int)ring * 31 + sector) < 0.35f + 0.9f * (torn - 0.3f);
        if (ring >= 0f && radius < limit && kept)
        {
            nearest = MathF.Min(nearest, MathF.Abs(wound - ring) * Gap / chord);
        }

        return nearest;
    }

    // Above 0 beyond the frame thread, on the side away from the corner.
    private static float Beyond(Vector2 uv)
    {
        var along = FrameLeft - FrameTop;
        var normal = Vector2.Normalize(new Vector2(along.Y, -along.X));
        return Vector2.Dot(uv - FrameTop, normal) * (Vector2.Dot(-FrameTop, normal) > 0f ? -1f : 1f);
    }

    // How far a spoke reaches from the hub along a direction before it meets a side or the frame thread.
    private static float Reach(Vector2 direction)
    {
        float reach = 1f;
        if (direction.Y < 0f)
        {
            reach = MathF.Min(reach, (Inset - Hub.Y) / direction.Y);
        }

        if (direction.X < 0f)
        {
            reach = MathF.Min(reach, (Inset - Hub.X) / direction.X);
        }

        var along = FrameLeft - FrameTop;
        var normal = new Vector2(along.Y, -along.X);
        float facing = Vector2.Dot(direction, normal);
        if (MathF.Abs(facing) > 1e-6f)
        {
            float t = Vector2.Dot(FrameTop - Hub, normal) / facing;
            if (t > 0f)
            {
                reach = MathF.Min(reach, t);
            }
        }

        return reach;
    }

    private static float Segment(Vector2 q, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = Math.Clamp(Vector2.Dot(q - a, ab) / ab.LengthSquared(), 0f, 1f);
        return (q - (a + ab * t)).Length();
    }

    // A number from 0 to 1 that depends only on the seed and the salt.
    private static float Unit(int seed, int salt)
    {
        uint h = unchecked(((uint)seed * 0x9E3779B1u) ^ ((uint)salt * 0x85EBCA77u + 0x1B873593u));
        h ^= h >> 15;
        h = unchecked(h * 0x2C1B3C6Du);
        h ^= h >> 12;
        h = unchecked(h * 0x297A2D39u);
        h ^= h >> 15;
        return (h >> 8) * (1f / (1 << 24));
    }
}
