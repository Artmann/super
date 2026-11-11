using System.Numerics;
using Super.Materials;

/// <summary>
/// One ivy leaf, 0.2 m across, for leaf cards: five lobes, a long one up the middle, two to the sides and two small
/// ones at the base, round a heart-shaped notch where the stalk joins. The stalk runs from the notch to the foot of the
/// image, so a card stands on its stalk. Pale veins run from the notch to each lobe's tip, with side veins off them,
/// sunk a little into the leaf. Dark green at the base to fresh green at the tips, yellowing in patches, and waxy.
/// Everything outside the leaf has an opacity of 0, so a rule's <c>alphaCutoff</c> cuts it out.
/// <see cref="MaterialGraph.Seed"/> gives each leaf its own lobes, so two seeds give the two sizes of leaf.
/// </summary>
public sealed class IvyLeaf : MaterialGraph
{
    /// <summary>Where the stalk meets the leaf, in fractions of the image, with row 0 at the top.</summary>
    public static readonly Vector2 Notch = new(0.5f, 0.74f);

    /// <summary>The lowest point of the stalk, a little above the image's foot so it doesn't wrap onto the top row.</summary>
    public const float StalkFoot = 0.985f;

    // How soft the leaf's edge is, as a fraction of the image: about two pixels at 1024.
    private const float Soft = 0.002f;

    public override Material Build()
    {
        var lobes = Lobes(Seed);

        // The leaf's outline as a distance-like field: above 0 inside, below 0 outside, made of the lobes joined
        // smoothly round the palm.
        var field = Pixel.Gray(p => Field(Leaf(p.Uv), lobes));
        var stalk = Pixel.Gray(p => Stalk(p.Uv));
        var leaf = field.Levels(-Soft, Soft);
        var opacity = leaf.Max(stalk);

        // The rim: 1 at the edge, fading to 0 a centimetre in, from the distance to the outside of the leaf. The leaf is
        // thickest in its middle and curls down a little at its edge.
        var rim = leaf.Invert().Distance(0.05f);

        // Veins: the main ones from the notch to each lobe's tip, side veins off them towards the tips, bent by
        // directional noise so they wander as real ones do, and a faint net between them from cell edges.
        var wander = Noise.Directional(scale: 24, angle: 0f, stretch: 3f, octaves: 2, seed: 2);
        var veins = Pixel.Gray(p => Veins(Leaf(p.Uv), lobes, p.Gray(0)), wander);
        var net = Noise.Cells(scale: 48, output: CellOutput.Edge, seed: 3).Levels(0f, 0.06f).Invert().Multiply(0.35f);
        var allVeins = veins.Max(net.Multiply(leaf));

        // Along each lobe, 0 at the notch and 1 at its tip, and the shiny band down the middle of each lobe.
        var along = Pixel.Gray(p => Along(Leaf(p.Uv), lobes));
        var band = Pixel.Gray(p => Band(Leaf(p.Uv), lobes));

        var height = rim.Invert().Remap(0.35f, 0.75f)
            .Subtract(allVeins.Multiply(0.18f))
            .Add(Noise.Perlin(scale: 16, octaves: 3, seed: 4).Remap(-0.05f, 0.05f))
            .Multiply(leaf)
            .Max(stalk.Multiply(0.6f));

        // Dark green at the base to fresh green at the tips. About a tenth of the leaf has started to yellow, in soft
        // Perlin patches. The veins and the very rim are paler.
        var green = along.GradientMap(Ramp.Of((0f, "#2f4a1e"), (0.45f, "#43662a"), (1f, "#6f9a3a")));
        var yellowing = Noise.Perlin(scale: 4, octaves: 3, seed: 5 + Seed).HistogramScan(0.1f, contrast: 0.35f);
        var color = green
            .Blend(Rgb.Of("#a3a23e"), mask: yellowing, opacity: 0.75f)
            .Blend(Rgb.Of("#9fb46c"), mask: allVeins, opacity: 0.7f)
            .Blend(Rgb.Of("#7d9a46"), mask: rim.Levels(0.8f, 1f), opacity: 0.35f)
            .Multiply(Noise.Perlin(scale: 32, octaves: 3, seed: 6).Remap(0.92f, 1.06f))
            .Blend(Rgb.Of("#5c6b2c"), mask: stalk.Multiply(leaf.Invert()));

        // Waxy: 0.55 over the leaf and 0.42 in a band down the middle of each lobe, where the cuticle catches the light,
        // a little rougher on the veins. Glossier, a leaf seen edge on mirrors the sky and turns white.
        var roughness = band.Remap(0.55f, 0.42f)
            .Add(allVeins.Multiply(0.08f))
            .Add(Noise.Perlin(scale: 24, octaves: 2, seed: 7).Remap(-0.03f, 0.03f));

        return new Material
        {
            Width = 0.2f,
            Depth = 0.0015f,
            Occlusion = 0.6f,
            Height = height,
            Color = color,
            Roughness = roughness,
            Opacity = opacity,
        };
    }

    /// <summary>One lobe: the way it points, how long it is and how wide at its base, in fractions of the image.</summary>
    private readonly record struct Lobe(Vector2 Along, Vector2 Across, float Length, float Width);

    // The five lobes for a seed: the middle one first, then the sides, then the small ones at the base.
    private static Lobe[] Lobes(int seed)
    {
        Lobe Make(float degrees, float length, float width, int salt)
        {
            float angle = (degrees + (Unit(seed, salt) - 0.5f) * 12f) * (MathF.PI / 180f);
            var along = new Vector2(DMath.Cos(angle), DMath.Sin(angle));
            var scale = 0.9f + 0.2f * Unit(seed, salt + 1);

            return new Lobe(along, new Vector2(-along.Y, along.X), length * scale, width * (0.92f + 0.16f * Unit(seed, salt + 2)));
        }

        // A little lopsided, as real leaves are: each side's lobes are drawn on their own.
        return
        [
            Make(90f, 0.62f, 0.27f, 10),
            Make(28f, 0.43f, 0.2f, 20),
            Make(152f, 0.43f, 0.2f, 30),
            Make(-18f, 0.28f, 0.15f, 40),
            Make(198f, 0.28f, 0.15f, 50),
        ];
    }

    // A point in the leaf's own space: across from the notch to the right, and up the image.
    private static Vector2 Leaf(Vector2 uv) => new(uv.X - Notch.X, Notch.Y - uv.Y);

    private static float Field(Vector2 q, Lobe[] lobes)
    {
        // The palm: a disc above the notch that the lobes grow out of.
        float field = 0.2f - (q - new Vector2(0f, 0.13f)).Length();
        foreach (var lobe in lobes)
        {
            field = SmoothMax(field, LobeField(q, lobe), 0.05f);
        }

        // The heart-shaped notch where the stalk joins, open to the foot of the leaf between the two small lobes.
        field = MathF.Min(field, SegmentDistance(q, new Vector2(0f, -0.04f), new Vector2(0f, -0.4f)) - 0.055f);

        // A margin that waves very slightly.
        float turn = DMath.Atan2(q.Y - 0.12f, q.X);
        return field + 0.0025f * DMath.Sin(turn * 17f);
    }

    // Above 0 inside one lobe: a blade that is widest at its base and runs straight in to a sharp tip.
    private static float LobeField(Vector2 q, Lobe lobe)
    {
        float t = Vector2.Dot(q, lobe.Along);
        float s = MathF.Abs(Vector2.Dot(q, lobe.Across));
        float u = t / lobe.Length;
        // Narrow where it leaves the palm, widest a third of the way out, then straight in to the tip. Never quite 0,
        // so the field stays below 0 along the lobe's line behind the notch.
        float rise = Math.Clamp((u + 0.12f) / 0.42f, 0f, 1f);
        float half = lobe.Width * MathF.Sqrt(rise) * DMath.Pow(MathF.Max(0f, 1f - u), 0.95f) - 0.01f;

        return MathF.Min(half - s, lobe.Length - t);
    }

    // The lobe this point belongs to: the one it is furthest inside.
    private static int Nearest(Vector2 q, Lobe[] lobes)
    {
        int best = 0;
        float most = float.MinValue;
        for (int i = 0; i < lobes.Length; i++)
        {
            float field = LobeField(q, lobes[i]);
            if (field > most)
            {
                most = field;
                best = i;
            }
        }

        return best;
    }

    // How far along its lobe a point is, blended between lobes by how far inside each it is, so the colour has no seam
    // where two lobes meet.
    private static float Along(Vector2 q, Lobe[] lobes)
    {
        float sum = 0f;
        float weights = 0f;
        foreach (var lobe in lobes)
        {
            float weight = DMath.Exp(Math.Clamp(LobeField(q, lobe), -0.3f, 0.3f) * 40f);
            sum += weight * Math.Clamp(Vector2.Dot(q, lobe.Along) / lobe.Length, 0f, 1f);
            weights += weight;
        }

        return sum / weights;
    }

    // 1 down the middle third of each lobe, fading out towards its edges.
    private static float Band(Vector2 q, Lobe[] lobes)
    {
        var lobe = lobes[Nearest(q, lobes)];
        float u = Math.Clamp(Vector2.Dot(q, lobe.Along) / lobe.Length, 0f, 1f);
        float half = lobe.Width * MathF.Max(0.05f, 1f - u);
        float s = MathF.Abs(Vector2.Dot(q, lobe.Across)) / half;

        return 1f - Math.Clamp((s - 0.25f) / 0.3f, 0f, 1f);
    }

    private static float Veins(Vector2 q, Lobe[] lobes, float wander)
    {
        float veins = 0f;
        foreach (var lobe in lobes)
        {
            // The main vein, from the notch to the tip, thinning as it goes.
            float t = Vector2.Dot(q, lobe.Along);
            float s = Vector2.Dot(q, lobe.Across);
            float u = t / lobe.Length;
            if (u is > -0.05f and < 0.97f)
            {
                float width = 0.0042f * (1f - u) + 0.0012f;
                veins = MathF.Max(veins, Ridge(s / width));
            }
        }

        // Side veins off the nearest lobe's main vein, leaving it at a slant towards the tip, every 4 mm or so, and
        // fading out before the edge.
        var near = lobes[Nearest(q, lobes)];
        float along = Vector2.Dot(q, near.Along);
        float across = MathF.Abs(Vector2.Dot(q, near.Across));
        float edge = near.Width * MathF.Max(0.02f, 1f - Math.Clamp(along / near.Length, 0f, 1f));
        if (along > 0f && across < edge)
        {
            float phase = (along - 0.9f * across) / 0.042f + (wander - 0.5f) * 0.9f;
            float line = MathF.Abs(phase - MathF.Round(phase)) * 0.042f;
            float fade = 1f - Math.Clamp((across / edge - 0.55f) / 0.4f, 0f, 1f);
            veins = MathF.Max(veins, 0.75f * fade * Ridge(line / 0.0012f));
        }

        return veins;
    }

    private static float Stalk(Vector2 uv)
    {
        // A stalk 2.4 mm thick, bending slightly as it comes down from the notch.
        if (uv.Y < Notch.Y - 0.02f || uv.Y > StalkFoot)
        {
            return 0f;
        }

        float down = (uv.Y - Notch.Y) / (StalkFoot - Notch.Y);
        float middle = Notch.X + 0.012f * down * down;
        float soft = MathF.Min(1f, (StalkFoot - uv.Y) / Soft);
        return Math.Clamp((0.006f - MathF.Abs(uv.X - middle)) / Soft + 0.5f, 0f, 1f) * soft;
    }

    private static float SegmentDistance(Vector2 q, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = Math.Clamp(Vector2.Dot(q - a, ab) / ab.LengthSquared(), 0f, 1f);
        return (q - (a + ab * t)).Length();
    }

    private static float Ridge(float x) => DMath.Exp(-x * x);

    private static float SmoothMax(float a, float b, float k)
    {
        float h = Math.Clamp(0.5f + 0.5f * (a - b) / k, 0f, 1f);
        return b + (a - b) * h + k * h * (1f - h);
    }

    // A number from 0 to 1 that depends only on the seed and the salt.
    private static float Unit(int seed, int salt)
    {
        uint h = unchecked(((uint)seed * 0x9E3779B1u) ^ ((uint)salt * 0x85EBCA77u));
        h ^= h >> 15;
        h = unchecked(h * 0x2C1B3C6Du);
        h ^= h >> 12;
        h = unchecked(h * 0x297A2D39u);
        h ^= h >> 15;
        return (h >> 8) * (1f / (1 << 24));
    }
}
