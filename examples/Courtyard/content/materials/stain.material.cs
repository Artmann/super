using Super.Materials;

/// <summary>
/// The small stains content/decals/courtyard.json lays round the yard, for projecting as a decal that spans the image
/// once. <see cref="Kind"/> picks which: "ring", the dark dirt that gathers round the foot of something stood a long
/// time in one place; "moss", a cushion of green on damp stone; "water", a patch of ground still wet and dark; and
/// "boot", one muddy boot print pressed into the ground, toe towards V 0. Each fades to nothing well inside the image,
/// so the box it is drawn in never shows.
/// </summary>
public sealed class StainPatch : MaterialGraph
{
    /// <summary>Which stain: "ring", "moss", "water" or "boot".</summary>
    public string Kind { get; init; } = "ring";

    /// <summary>For a ring, a square foot such as a crate's rather than a round one such as a barrel's.</summary>
    public bool Square { get; init; }

    public override Material Build()
    {
        var stain = Kind switch
        {
            "ring" => Ring(),
            "moss" => Moss(),
            "water" => Water(),
            "boot" => Boot(),
            _ => throw new ArgumentException($"StainPatch's Kind is \"{Kind}\", but it must be ring, moss, water or boot."),
        };

        // Moss reaches the edges at the middle of each side, and a torn or blurred rim can leave a faint trace there
        // in the others, which the decal's box would cut off in a straight line. So every kind is faded out over the
        // band from 15 % of the image in to 5 %, and is nothing at all outside that.
        return stain with { Opacity = stain.Opacity!.Multiply(Border()) };
    }

    // 1 from 15 % of the image in from every edge, easing to 0 at 5 % and 0 beyond.
    private static Gray Border()
    {
        return Pixel.Gray(p =>
        {
            float border = MathF.Min(MathF.Min(p.Uv.X, 1f - p.Uv.X), MathF.Min(p.Uv.Y, 1f - p.Uv.Y));
            float t = Math.Clamp((border - 0.05f) / 0.1f, 0f, 1f);
            return t * t * (3f - 2f * t);
        });
    }

    // How far a point is from the middle of the image, 0 there and 1 at the middle of each edge, round or square.
    private Gray Distance()
    {
        bool square = Square;
        return Pixel.Gray(p =>
        {
            float x = MathF.Abs(p.Uv.X - 0.5f) * 2f;
            float y = MathF.Abs(p.Uv.Y - 0.5f) * 2f;
            return square ? MathF.Max(x, y) : MathF.Sqrt(x * x + y * y);
        });
    }

    // Dirt trodden and washed in round a foot: darkest in a band just outside where the foot stands, at 0.6 of the
    // way out, fading slowly outwards and quickly in under the foot, torn by noise so it isn't a drawn circle.
    private Material Ring()
    {
        var torn = Noise.Perlin(scale: 9, octaves: 4, seed: Seed + 10).Remap(-0.12f, 0.12f);
        var band = Pixel.Gray(p =>
        {
            float d = p.Gray(0) + p.Gray(1);
            float rim = d < 0.6f ? MathF.Max(0f, 1f - (0.6f - d) / 0.25f) : MathF.Max(0f, 1f - (d - 0.6f) / 0.3f);
            return rim * rim * (3f - 2f * rim);
        }, Distance(), torn);
        var flecks = Noise.Perlin(scale: 40, octaves: 3, seed: Seed + 11).Levels(0.3f, 0.7f).Remap(0.6f, 1f);
        var color = Noise.Perlin(scale: 20, octaves: 3, seed: Seed + 12).GradientMap(Ramp.Of("#211a14", "#2e251c", "#3a3024"));
        return new Material
        {
            Width = 1f,
            Depth = 0.002f,
            Color = color,
            Opacity = band.Multiply(flecks).Multiply(0.8f),
            Roughness = Noise.Perlin(scale: 16, octaves: 2, seed: Seed + 13).Remap(0.8f, 0.95f),
        };
    }

    // A cushion of moss: clumps of fine fronds, fullest in the middle and broken into islands towards the edge, a
    // little raised so they catch the light.
    private Material Moss()
    {
        var clumps = Noise.Perlin(scale: 7, octaves: 4, seed: Seed + 20);
        var cover = Pixel.Gray(p =>
        {
            float edge = 1f - p.Gray(0);
            return Math.Clamp(edge * 1.6f + (p.Gray(1) - 0.5f) * 1.4f - 0.15f, 0f, 1f);
        }, Distance(), clumps).Levels(0.25f, 0.6f);
        var fronds = Noise.Cells(scale: 90, output: CellOutput.Edge, seed: Seed + 21).Levels(0f, 0.4f);
        var height = cover.Multiply(fronds.Remap(0.6f, 1f)).Blur(0.002f);
        var color = Noise.Perlin(scale: 18, octaves: 3, seed: Seed + 22)
            .GradientMap(Ramp.Of("#25300f", "#3b4a17", "#56661f", "#6c7426"))
            .Multiply(fronds.Remap(0.7f, 1.05f));
        return new Material
        {
            Width = 0.4f,
            Depth = 0.004f,
            Color = color,
            Height = height,
            Opacity = cover.Multiply(fronds.Remap(0.55f, 1f)),
            Roughness = fronds.Remap(0.95f, 0.78f),
        };
    }

    // A patch still wet after rain: dark and glossy, with a soft edge that has dried back in lobes.
    private Material Water()
    {
        var lobes = Noise.Perlin(scale: 5, octaves: 4, seed: Seed + 30).Remap(-0.25f, 0.25f);
        var wet = Pixel.Gray(p => Math.Clamp((0.8f - p.Gray(0) - p.Gray(1)) / 0.25f, 0f, 1f), Distance(), lobes);
        return new Material
        {
            Width = 1f,
            Depth = 0.002f,
            // As dark as wet earth gets, about sRGB 50; the opacity keeps it from covering the ground's own colour.
            Color = Noise.Perlin(scale: 12, octaves: 3, seed: Seed + 31).GradientMap(Ramp.Of("#2f2921", "#3a332a")),
            Opacity = wet.Multiply(0.55f),
            Roughness = wet.Remap(0.7f, 0.12f),
        };
    }

    // One boot print, toe towards V 0: a broad sole and a separate heel, pressed in, with the tread's bars across them
    // and mud squeezed up a little round the rim.
    private Material Boot()
    {
        var print = Pixel.Gray(p =>
        {
            // The sole fills V 0.08 to 0.62 and the heel V 0.7 to 0.92, in an image a third as wide as it is long.
            float x = (p.Uv.X - 0.5f) / 0.32f;
            float sole = Oval(x, (p.Uv.Y - 0.36f) / 0.28f);
            float heel = Oval(x * 1.12f, (p.Uv.Y - 0.81f) / 0.11f);
            return MathF.Max(sole, heel);
        });
        var torn = Noise.Perlin(scale: 14, octaves: 3, seed: Seed + 40).Remap(-0.08f, 0.08f);
        var pressed = print.Add(torn).Levels(0.35f, 0.5f);
        var tread = Pixel.Gray(p => MathF.Sin(p.Uv.Y * 90f) > 0.2f ? 1f : 0.55f);
        var rim = pressed.Blur(0.02f).Multiply(pressed.Invert());
        return new Material
        {
            Width = 0.35f,
            Depth = 0.006f,
            Color = Noise.Perlin(scale: 30, octaves: 3, seed: Seed + 41).GradientMap(Ramp.Of("#241c15", "#33281e")),
            Height = pressed.Multiply(tread).Invert().Add(rim.Multiply(0.4f)).Multiply(0.8f),
            Opacity = pressed.Multiply(0.75f).Max(rim.Multiply(1.5f).Min(0.4f)),
            Roughness = pressed.Remap(0.9f, 0.55f),
        };
    }

    // 1 inside an oval of unit radii, falling to 0 just outside it.
    private static float Oval(float x, float y)
    {
        float d = MathF.Sqrt(x * x + y * y);
        return Math.Clamp((1.1f - d) / 0.2f, 0f, 1f);
    }
}
