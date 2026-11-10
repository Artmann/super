using Super.Materials;

/// <summary>
/// Trodden yard mud after rain, 4 m across: coarse clods and crumbs, pebbles of three sizes, flecks of straw, dark rotted
/// patches and fallen leaves pressed into it, dull where it has dried and glossy where water still lies in the hollows.
/// </summary>
public sealed class Mud : MaterialGraph
{
    public override Material Build()
    {
        // Broad swells, then coarse clods about 30 cm across pushed up between prints, and crumbs between those.
        var swell = Noise.Perlin(scale: 6, octaves: 5).Remap(0.3f, 0.6f);
        // A clod is a cell, rounded off towards the cracks between it and the next, each standing its own height.
        var clods = Clods(scale: 14, seed: 1).Multiply(0.16f);
        var crumbs = Clods(scale: 64, seed: 3).Multiply(0.06f);
        var grit = Noise.White().Blur(0.0006f).Remap(-0.025f, 0.025f);
        var ground = swell.Add(clods).Add(crumbs).Add(grit);

        // Pebbles of three sizes, about a centimetre, 2 to 3 cm and 4 to 7 cm. Each stamp is a dome bent out of round,
        // flattened on top, so they read as stones rather than beads.
        var stone = Shape.Of(ShapeKind.Bell).Warp(Noise.Perlin(scale: 3, octaves: 2, seed: 5), amount: 0.05f).Levels(0f, 0.55f);
        var small = stone.Splatter(count: 1400, sizeMin: 0.0016f, sizeMax: 0.0032f, seed: 11);
        var medium = stone.Splatter(count: 260, sizeMin: 0.004f, sizeMax: 0.0075f, seed: 12);
        var large = stone.Splatter(count: 40, sizeMin: 0.01f, sizeMax: 0.0175f, seed: 13);
        // They sit on the broad swell, half sunk, so they stand out of the mud round them but not out of the clods. Only
        // a stone is lifted: between the stones the top layer is the swell let down by the grit's depth, so it never
        // wins in the cracks between clods.
        var pebbles = small.Multiply(0.35f).Max(medium.Multiply(0.5f)).Max(large.Multiply(0.65f));
        var lift = pebbles.Levels(0f, 0.02f).Multiply(0.13f);
        var withPebbles = Gray.HeightBlend(ground, swell.Add(pebbles).Add(lift), offset: 0.45f, contrast: 0.95f);

        // Straw trodden in: little clumps of short parallel strokes, each clump turned its own way.
        var strawClump = Noise.Directional(scale: 12, stretch: 6, octaves: 1, seed: 6)
            .Levels(0.6f, 0.68f)
            .Multiply(Shape.Of(ShapeKind.Bell).Levels(0f, 0.5f));
        var straw = strawClump.Splatter(count: 320, sizeMin: 0.01f, sizeMax: 0.02f, seed: 14)
            .Multiply(Noise.Perlin(scale: 5, octaves: 2, seed: 7).Levels(0.35f, 0.55f));

        // Fallen leaves pressed flat into the mud, 4 to 8 cm long, browned and darker than the dry earth.
        var leaves = Leaf().Splatter(count: 150, sizeMin: 0.012f, sizeMax: 0.022f, seed: 15);
        var leafMask = leaves.Levels(0.05f, 0.2f);
        var height = withPebbles.Height
            .Blend(withPebbles.Height.Add(0.02f), mask: straw)
            .Blend(ground.Add(leaves.Multiply(0.04f)), mask: leafMask);

        // Water lies in the low places, so they're dark and glossy.
        var wet = height.Invert().Levels(0.45f, 0.65f).Multiply(Noise.Perlin(scale: 8, octaves: 3, seed: 8).Levels(0.3f, 0.6f));

        // The earth from dark umber to a dry grey-brown, darker in the hollows, paler in broad drier patches, and
        // darker again where rotted matter is mixed in.
        var dry = Noise.Perlin(scale: 3, octaves: 3, seed: 9).Levels(0.35f, 0.75f);
        var tone = ground.Levels(0.25f, 0.75f).Multiply(0.75f).Add(dry.Multiply(0.25f));
        var earth = tone.GradientMap(Ramp.Of("#3a2a1c", "#4c3a29", "#5f4b37", "#7a6248"));
        var organic = Noise.Perlin(scale: 10, octaves: 4, seed: 10).Levels(0.5f, 0.75f);
        earth = earth.Blend(Rgb.Of("#241a12"), mask: organic, opacity: 0.3f);

        var pebbleColor = withPebbles.Mask.Multiply(pebbles).FloodFill(threshold: 0.02f).RandomGray()
            .GradientMap(Ramp.Of("#4f4a44", "#6a645b", "#7d7468", "#5a5048"));
        var strawColor = Noise.White(seed: 16).Blur(0.001f).GradientMap(Ramp.Of("#7e7052", "#a29271"));
        var leafColor = leaves.FloodFill(threshold: 0.05f).RandomGray()
            .GradientMap(Ramp.Of("#33271c", "#4c3824", "#5f462c", "#6c5636"));

        var color = earth
            .Blend(pebbleColor, mask: withPebbles.Mask)
            .Blend(strawColor, mask: straw.Multiply(0.85f))
            .Blend(leafColor, mask: leafMask);
        color = color.Blend(color.Multiply(Gray.Of(0.6f)), mask: wet);

        return new Material
        {
            Width = 4f,
            Depth = 0.04f,
            Occlusion = 0.8f,
            Height = height,
            Color = color,
            Roughness = wet.Remap(0.75f, 0.3f)
                .Blend(Gray.Of(0.55f), mask: withPebbles.Mask.Multiply(0.6f))
                .Add(Noise.Perlin(scale: 30, octaves: 3, seed: 17).Remap(-0.04f, 0.04f)),
        };
    }

    /// <summary>Lumps <paramref name="scale"/> to the image, rounded off into the cracks between them, each its own height.</summary>
    private static Gray Clods(int scale, int seed)
    {
        var bent = Noise.Perlin(scale: scale, octaves: 2, seed: seed + 100);
        var rounded = Noise.Cells(scale: scale, output: CellOutput.Edge, seed: seed).Warp(bent, amount: 0.002f).Levels(0f, 0.35f, gamma: 0.7f);
        var own = Noise.Cells(scale: scale, output: CellOutput.Id, seed: seed).Warp(bent, amount: 0.002f).Remap(0.5f, 1f);
        return rounded.Multiply(own);
    }

    /// <summary>One leaf lying along the image, pointed at both ends, with its edges curled up a little and a midrib.</summary>
    private static Gray Leaf()
    {
        return Pixel.Gray(p =>
        {
            var t = (p.Uv.X - 0.5f) / 0.45f;
            var v = p.Uv.Y - 0.5f;
            // Widest a little towards the stalk.
            var half = 0.2f * (1f - t * t) * (1f - 0.25f * t);
            if (half <= 0f)
            {
                return 0f;
            }

            var across = (v < 0f ? -v : v) / half;
            if (across >= 1f)
            {
                return 0f;
            }

            var rib = across < 0.08f ? 0.15f : 0f;
            return 0.6f + 0.4f * across * across - rib;
        });
    }
}
