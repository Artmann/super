using System.Numerics;
using Super.Materials;

/// <summary>
/// Grass blades for blade cards, 0.3 m across: a row of blades side by side, each standing in its own column of the
/// image with its foot on the image's foot and its tip towards the top. A card wears one column, its foot on the
/// column's foot. Each blade is a strip that tapers to a point, folded along a pale rib down its middle that shows in
/// the normal, green at its foot and drying to straw towards its tip. Everything outside the blades has an opacity of
/// 0, so a rule's <c>alphaCutoff</c> cuts it out.
/// <see cref="Species"/> picks the grass: <c>meadow</c>, long narrow blades; <c>broad</c>, short wide lush ones; or
/// <c>dry</c>, thin straw stalks that end in a seed head of spikelets with fine awns.
/// </summary>
public sealed class GrassBlade : MaterialGraph
{
    /// <summary>The grass: <c>meadow</c>, <c>broad</c> or <c>dry</c>.</summary>
    public string Species { get; init; } = "meadow";

    /// <summary>How each species is drawn, all in fractions of the image unless a name says otherwise.</summary>
    /// <param name="Columns">How many blades stand side by side across the image.</param>
    /// <param name="Width">Half the blade's widest width, as a share of its column.</param>
    /// <param name="MinLength">The shortest blade, from the image's foot.</param>
    /// <param name="MaxLength">The longest.</param>
    /// <param name="Taper">The share of the blade's length over which it narrows to its point.</param>
    /// <param name="Head">The share of the stalk's length that is seed head, or 0 for a leaf blade.</param>
    public sealed record Look(int Columns, float Width, float MinLength, float MaxLength, float Taper, float Head, Ramp Colors);

    /// <summary>The species' look, or an error that lists the species there are.</summary>
    public static Look Of(string species) => species switch
    {
        // Long narrow blades, about 5 mm wide and 25 cm long, a slightly yellow olive meadow green, darker at the
        // foot, with dry straw tips. The leaves let the sun through, which deepens their green again, so it is a
        // muted one.
        "meadow" => new Look(24, 0.3f, 0.78f, 0.97f, 0.3f, 0f,
            Ramp.Of((0f, "#2f4020"), (0.12f, "#374c22"), (0.55f, "#5a6632"), (0.82f, "#7c7f46"), (1f, "#a8996a"))),
        // Short wide blades, about 1.5 cm wide and 18 cm long, a deeper lusher green that only browns at the very tip.
        "broad" => new Look(12, 0.33f, 0.5f, 0.7f, 0.3f, 0f,
            Ramp.Of((0f, "#2c3a1d"), (0.1f, "#304622"), (0.6f, "#435c2c"), (0.9f, "#5f6e3c"), (1f, "#8a8259"))),
        // Dry stalks, about 2 mm thick, straw all over, the top quarter a seed head about a centimetre wide.
        "dry" => new Look(16, 0.07f, 0.85f, 0.97f, 0.05f, 0.25f,
            Ramp.Of((0f, "#7d7c4a"), (0.3f, "#a29561"), (0.75f, "#c0ad7a"), (1f, "#b39c68"))),
        _ => throw new ArgumentException($"Species must be meadow, broad or dry, not {species}.", nameof(Species)),
    };

    // How soft the blades' edges are, as a fraction of the image: about a pixel and a half at 1024.
    private const float Soft = 0.0015f;

    public override Material Build()
    {
        var look = Of(Species);
        int seed = Seed;

        var blade = Pixel.Gray(p => Field(p.Uv, look, seed));
        var opacity = blade.Levels(-Soft, Soft);
        // 0 at a blade's foot and 1 at its tip, and its own random value, each per column.
        var along = Pixel.Gray(p => Along(p.Uv, look, seed));
        var tone = Pixel.Gray(p => Unit(seed, Column(p.Uv, look) * 16 + 5));
        // Another per blade, for its hue: some blades warmer and yellower, some cooler and bluer.
        var warmth = Pixel.Gray(p => Unit(seed, Column(p.Uv, look) * 16 + 6));
        var rib = Pixel.Gray(p => Rib(p.Uv, look, seed));
        var head = Pixel.Gray(p => look.Head > 0f && Along(p.Uv, look, seed) > 1f - look.Head ? 1f : 0f);

        // Fine veins running the length of the blade, and the blade folded along its rib, so it is highest down the
        // middle. Spikelets are little domes.
        var veins = Noise.Directional(scale: 220, angle: 90f, stretch: 16f, octaves: 2, seed: 2).Levels(0.3f, 0.7f);
        var fold = blade.Levels(-Soft, 0.012f);
        var height = fold.Multiply(0.55f)
            .Add(rib.Multiply(0.3f))
            .Add(veins.Multiply(0.08f))
            .Multiply(opacity);

        // Green at the foot drying to straw towards the tip, each blade a little lighter or darker, a little more or
        // less dry and a little yellower or bluer, the rib paler, and here and there a rust-brown spot.
        var dryness = along.Add(tone.Remap(-0.12f, 0.12f));
        var spots = Noise.Cells(scale: 90, seed: 3).Levels(0f, 0.12f).Invert()
            .Multiply(Noise.Perlin(scale: 6, octaves: 2, seed: 4).Levels(0.62f, 0.72f));
        var color = dryness.GradientMap(look.Colors)
            .Blend(Rgb.Of("#6c6a34"), mask: warmth.Levels(0.55f, 1f), opacity: 0.3f)
            .Blend(Rgb.Of("#394d33"), mask: warmth.Invert().Levels(0.55f, 1f), opacity: 0.3f)
            .Multiply(tone.Remap(0.86f, 1.1f))
            .Blend(dryness.GradientMap(look.Colors).Multiply(Gray.Of(1.25f)), mask: rib, opacity: 0.45f)
            .Blend(Rgb.Of("#7a5a32"), mask: spots, opacity: 0.6f)
            .Multiply(veins.Remap(0.94f, 1.04f))
            .Blend(Rgb.Of("#9c8350"), mask: head.Multiply(Noise.Perlin(scale: 40, octaves: 2, seed: 6)), opacity: 0.6f);

        // A waxy blade, smoother on the rib, rougher where it has dried.
        var roughness = dryness.Levels(0.4f, 1f).Remap(0.45f, 0.75f)
            .Subtract(rib.Multiply(0.08f))
            .Add(Noise.Perlin(scale: 30, octaves: 2, seed: 5).Remap(-0.03f, 0.03f));

        return new Material
        {
            Width = 0.3f,
            Depth = 0.001f,
            Occlusion = 0.4f,
            Height = height,
            Color = color,
            Roughness = roughness,
            Opacity = opacity,
        };
    }

    /// <summary>The column a point is in, from 0 at the left.</summary>
    public static int Column(Vector2 uv, Look look) => Math.Clamp((int)(uv.X * look.Columns), 0, look.Columns - 1);

    // One blade's own shape, from its column: its length, how far its tip leans and how wide it is.
    private static (float Length, float Lean, float Width) Blade(int column, Look look, int seed) =>
        (look.MinLength + (look.MaxLength - look.MinLength) * Unit(seed, column * 16),
         (Unit(seed, column * 16 + 1) - 0.5f) * 0.5f,
         look.Width * (0.85f + 0.15f * Unit(seed, column * 16 + 2)));

    private static float Along(Vector2 uv, Look look, int seed)
    {
        var blade = Blade(Column(uv, look), look, seed);
        return Math.Clamp((1f - uv.Y) / blade.Length, 0f, 1f);
    }

    // Across the column, in fractions of the image, from the blade's middle line, which bends as the tip leans.
    private static float Across(Vector2 uv, Look look, (float Length, float Lean, float Width) blade, float h)
    {
        float inColumn = uv.X * look.Columns - Column(uv, look) - 0.5f;
        return (inColumn - blade.Lean * h * h) / look.Columns;
    }

    // Above 0 inside the blade, roughly the distance to its edge in fractions of the image.
    private static float Field(Vector2 uv, Look look, int seed)
    {
        var blade = Blade(Column(uv, look), look, seed);
        float h = (1f - uv.Y) / blade.Length;
        float x = Across(uv, look, blade, h);
        float columnWidth = 1f / look.Columns;

        // Ends: the tip, and a little above the image's foot so nothing wraps onto the top row.
        float ends = MathF.Min((1f - h) * blade.Length, uv.Y < 0.995f ? 1f : -1f);

        if (look.Head > 0f && h > 1f - look.Head)
        {
            return MathF.Min(Head(x, h, blade, look, seed), ends + 0.02f);
        }

        // The width: a narrower sheath at the foot, then full width, then narrowing over the taper to a point.
        float sheath = 0.8f + 0.2f * Math.Clamp(h / 0.08f, 0f, 1f);
        float taper = DMath.Pow(Math.Clamp((1f - h) / look.Taper, 0f, 1f), 0.8f);
        float half = blade.Width * columnWidth * sheath * taper;

        return MathF.Min(half - MathF.Abs(x), ends);
    }

    // A seed head: the stalk with spikelets on alternate sides, each leaning out and up, with an awn off its tip.
    private static float Head(float x, float h, (float Length, float Lean, float Width) blade, Look look, int seed)
    {
        float stalk = blade.Width * 0.7f / look.Columns - MathF.Abs(x);
        float y = h * blade.Length;
        float top = blade.Length;
        float start = top * (1f - look.Head);
        const float Spacing = 0.016f;

        float best = stalk;
        int first = (int)MathF.Floor((y - start) / Spacing) - 2;
        for (int i = Math.Max(0, first); i <= first + 4; i++)
        {
            float at = start + (i + 0.5f) * Spacing;
            if (at > top - 0.01f)
            {
                break;
            }

            // Spikelets shrink towards the head's tip, and alternate sides.
            float shrink = 1f - 0.45f * (at - start) / (top - start);
            float side = i % 2 == 0 ? 1f : -1f;
            float tilt = 0.45f + 0.15f * Unit(seed, i * 3 + 9);
            var direction = new Vector2(side * DMath.Sin(tilt), DMath.Cos(tilt));
            var normal = new Vector2(-direction.Y, direction.X);
            float length = 0.026f * shrink;
            var centre = new Vector2(side * 0.004f, at) + direction * (length * 0.5f);
            var d = new Vector2(x, y) - centre;
            float along = Vector2.Dot(d, direction) / (length * 0.5f);
            float across = Vector2.Dot(d, normal) / (0.0055f * shrink);
            float spikelet = (1f - MathF.Sqrt(along * along + across * across)) * 0.0045f * shrink;

            // The awn: a hair 0.4 mm thick running on from the spikelet's tip.
            float awnAlong = Vector2.Dot(d, direction) - length * 0.5f;
            float awn = awnAlong is > 0f and < 0.016f ? 0.0006f - MathF.Abs(Vector2.Dot(d, normal)) : -1f;

            best = MathF.Max(best, MathF.Max(spikelet, awn));
        }

        // The head ends in one last spikelet's awn, straight up.
        float crown = y > top - 0.01f ? 0.0006f - MathF.Abs(x) : -1f;
        return MathF.Max(best, crown);
    }

    // 1 down the blade's middle, fading to 0 a fifth of the way to its edge.
    private static float Rib(Vector2 uv, Look look, int seed)
    {
        var blade = Blade(Column(uv, look), look, seed);
        float h = Math.Clamp((1f - uv.Y) / blade.Length, 0f, 1f);
        float x = Across(uv, look, blade, h) * look.Columns / (blade.Width * (look.Head > 0f ? 0.7f : 1f));
        float ridge = x / 0.18f;
        return DMath.Exp(-ridge * ridge);
    }

    // A number from 0 to 1 that depends only on the seed and the salt.
    private static float Unit(int seed, int salt)
    {
        uint h = unchecked(((uint)seed * 0x9E3779B1u) ^ ((uint)salt * 0x85EBCA77u + 0x68E31DA4u));
        h ^= h >> 15;
        h = unchecked(h * 0x2C1B3C6Du);
        h ^= h >> 12;
        h = unchecked(h * 0x297A2D39u);
        h ^= h >> 15;
        return (h >> 8) * (1f / (1 << 24));
    }
}
