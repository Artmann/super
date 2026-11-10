using System.Numerics;
using Super.Materials;

/// <summary>
/// A fallen oak leaf, dry and brown, 0.15 m across, for a leaf card: its stalk at the image's foot and its tip at the
/// top. The outline has rounded lobes down both sides, a midrib with a vein out to each lobe, a crumbled margin and a
/// few holes eaten through it. It has dried tan with darker brown blotches and a paler margin, and its edges curl up in
/// the height. Everything outside the leaf and in its holes has an opacity of 0, so a rule's <c>alphaCutoff</c> cuts
/// it out. Each seed bends it, sets its lobes and eats different holes.
/// </summary>
public sealed class DeadLeaf : MaterialGraph
{
    /// <summary>Where the leaf's blade starts above its stalk, and where its tip is, in rows of the image.</summary>
    public const float Base = 0.86f;

    /// <inheritdoc cref="Base"/>
    public const float Tip = 0.05f;

    /// <summary>The foot of the stalk, in rows of the image.</summary>
    public const float StalkFoot = 0.985f;

    private const float Soft = 0.002f;

    public override Material Build()
    {
        int seed = Seed;
        var form = Form.Of(seed);

        // The margin is crumbled: a little of it is broken off here and there.
        var crumble = Noise.Perlin(scale: 40, octaves: 3, seed: 2);
        var field = Pixel.Gray(p => Field(p.Uv, form) - 0.006f * MathF.Max(0f, p.Gray(0) - 0.55f) * 5f, crumble);
        var leaf = field.Levels(-Soft, Soft);
        var stalk = Pixel.Gray(p => Stalk(p.Uv, form));

        // Holes eaten through it: round-ish gaps from a cell pattern, only where a coarse mask lets them.
        var holes = Noise.Cells(scale: 16, seed: 3).Levels(0.05f, 0.09f).Invert()
            .Multiply(Noise.Perlin(scale: 5, octaves: 2, seed: 4).Levels(0.66f, 0.68f));
        var opacity = leaf.Multiply(holes.Invert()).Max(stalk);

        var midrib = Pixel.Gray(p => Midrib(p.Uv, form));
        var veins = Pixel.Gray(p => Veins(p.Uv, form)).Max(midrib);
        var rim = field.Levels(0f, 0.035f).Invert().Multiply(leaf);

        // Dry tan, browner in blotches, paler at the margin and on the veins.
        var blotch = Noise.Perlin(scale: 7, octaves: 4, seed: 5).HistogramScan(0.5f, 0.4f);
        var color = Noise.Perlin(scale: 18, octaves: 3, seed: 6)
            .GradientMap(Ramp.Of("#8a5a30", "#a06c3a", "#94622f"))
            .Blend(Rgb.Of("#5e3a1e"), mask: blotch, opacity: 0.75f)
            .Blend(Rgb.Of("#b58e5c"), mask: rim, opacity: 0.55f)
            .Blend(Rgb.Of("#c09a68"), mask: veins, opacity: 0.6f)
            .Blend(Rgb.Of("#6e4a28"), mask: stalk, opacity: 1f);

        // The edges curl up, the veins stand a little proud, and the blade is wrinkled.
        var wrinkles = Noise.Perlin(scale: 14, octaves: 4, seed: 7);
        var height = rim.Multiply(0.45f)
            .Add(veins.Multiply(0.25f))
            .Add(wrinkles.Multiply(0.3f))
            .Multiply(opacity);

        return new Material
        {
            Width = 0.15f,
            Depth = 0.003f,
            Occlusion = 0.5f,
            Height = height,
            Color = color,
            Roughness = blotch.Remap(0.72f, 0.88f).Subtract(veins.Multiply(0.06f))
                .Add(Noise.Perlin(scale: 30, octaves: 2, seed: 8).Remap(-0.04f, 0.04f)),
            Opacity = opacity,
        };
    }

    /// <summary>One leaf's own shape: how its middle bends, how many lobes it has and where they sit on each side.</summary>
    private sealed record Form(float Bend, float Lobes, float LeftPhase, float RightPhase, float Width)
    {
        public static Form Of(int seed) => new(
            (Unit(seed, 1) - 0.5f) * 0.12f,
            4f + Unit(seed, 2),
            Unit(seed, 3),
            Unit(seed, 4),
            0.3f + 0.04f * Unit(seed, 5));
    }

    // 0 at the blade's base and 1 at its tip.
    private static float Along(Vector2 uv) => (Base - uv.Y) / (Base - Tip);

    // Across, from the midrib, which bends to one side.
    private static float Across(Vector2 uv, Form form, float t) => uv.X - 0.5f - form.Bend * t * t;

    // 1 at a lobe's tip, falling round to 0 in the narrow sinus between two lobes.
    private static float Lobe(float t, float x, Form form)
    {
        float phase = x < 0f ? form.LeftPhase : form.RightPhase;
        float f = t * form.Lobes + phase;
        float s = 2f * (f - MathF.Round(f));
        return MathF.Sqrt(MathF.Max(0f, 1f - s * s));
    }

    // Above 0 inside the blade, roughly the distance to its edge in fractions of the image.
    private static float Field(Vector2 uv, Form form)
    {
        float t = Along(uv);
        if (t <= 0f || t >= 1f)
        {
            // Beyond the tip or the base it falls away across as well as along, so the rows just past them are clear
            // all the way to the card's sides, not only near zero.
            return MathF.Min(t, 1f - t) * (Base - Tip) - MathF.Abs(uv.X - 0.5f);
        }

        float x = Across(uv, form, t);

        // Widest a little above the middle, rounded at the tip, narrowing to the stalk at the base, with the lobes
        // cut deep into it.
        float outline = DMath.Sin(MathF.PI * DMath.Pow(t, 0.8f)) * DMath.Pow(Math.Clamp(t / 0.3f, 0f, 1f), 0.6f);
        float lobe = Lobe(t, x, form);
        float half = form.Width * outline * (0.5f + 0.5f * lobe);
        return MathF.Min(half - MathF.Abs(x), MathF.Min(t, 1f - t) * 0.25f);
    }

    private static float Stalk(Vector2 uv, Form form)
    {
        if (uv.Y < Base - 0.02f || uv.Y > StalkFoot)
        {
            return 0f;
        }

        float half = 0.007f * (1f - 0.3f * (uv.Y - Base) / (StalkFoot - Base));
        return Math.Clamp((half - MathF.Abs(uv.X - 0.5f)) / Soft * 0.5f + 0.5f, 0f, 1f);
    }

    // 1 down the middle, fading towards the tip.
    private static float Midrib(Vector2 uv, Form form)
    {
        float t = Along(uv);
        if (t < 0f || t > 1f)
        {
            return 0f;
        }

        float x = Across(uv, form, t) / (0.006f * (1f - 0.7f * t));
        return DMath.Exp(-x * x);
    }

    // A vein from the midrib out and up to each lobe's tip.
    private static float Veins(Vector2 uv, Form form)
    {
        float t = Along(uv);
        if (t < 0f || t > 1f)
        {
            return 0f;
        }

        float x = Across(uv, form, t);
        float phase = x < 0f ? form.LeftPhase : form.RightPhase;
        // Along a vein, t * Lobes + phase - |x| * Rise is a whole number, so it reaches the edge at a lobe's tip.
        const float Rise = 4.5f;
        float wound = t * form.Lobes + phase - MathF.Abs(x) * Rise;
        float off = (wound - MathF.Round(wound)) / (form.Lobes * 0.003f);
        return DMath.Exp(-off * off) * (1f - 0.6f * MathF.Abs(x) / form.Width);
    }

    // A number from 0 to 1 that depends only on the seed and the salt.
    private static float Unit(int seed, int salt)
    {
        uint h = unchecked(((uint)seed * 0x9E3779B1u) ^ ((uint)salt * 0x85EBCA77u + 0x2545F491u));
        h ^= h >> 15;
        h = unchecked(h * 0x2C1B3C6Du);
        h ^= h >> 12;
        h = unchecked(h * 0x297A2D39u);
        h ^= h >> 15;
        return (h >> 8) * (1f / (1 << 24));
    }
}
