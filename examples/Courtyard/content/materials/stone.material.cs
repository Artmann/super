using Super.Materials;

/// <summary>Weathered grey-brown stone with pits, hairline cracks, flecks of grit, spots of lichen and patches of moss, for the plinth and the sill. Its spalls have a bevel highlight on their rims and its high spots are rubbed lighter.</summary>
public sealed class Stone : MaterialGraph
{
    public override Material Build()
    {
        // Broad lumps and small pits, with a fine grain over them.
        var lumps = Noise.Perlin(scale: 4, octaves: 6).Remap(0.3f, 0.7f);
        var pits = Noise.Cells(scale: 18, output: CellOutput.F1).Levels(0f, 0.35f).Multiply(0.12f);
        var grain = Noise.White().Blur(0.0008f).Remap(-0.04f, 0.04f);

        // Hairline cracks along the borders of cells, only in some places, so the stone reads
        // sharp even where the sun grazes it.
        var cracks = Noise.Cells(scale: 7, output: CellOutput.Edge).Levels(0f, 0.035f).Invert()
            .Multiply(Noise.Perlin(scale: 5, octaves: 3).Levels(0.45f, 0.65f));
        var height = lumps.Add(grain)
            .Subtract(Noise.Perlin(scale: 20, octaves: 3).Levels(0.55f, 0.75f).Multiply(0.1f))
            .Add(pits)
            .Subtract(cracks.Multiply(0.12f));

        // Spalls: flat-bottomed scars a few centimetres across where a flake has broken off, with
        // a sharp rim, so the stone has detail between its broad lumps and its fine grain.
        var spalls = Noise.Perlin(scale: 14, octaves: 4).Levels(0.64f, 0.68f);
        height = height.Subtract(spalls.Multiply(0.18f));

        // Moss sits in the low places.
        var moss = Noise.Perlin(scale: 5, octaves: 5).Levels(0.55f, 0.75f).Multiply(height.Invert().Levels(0.35f, 0.6f));

        // Small round spots of pale lichen, gathered in patches.
        var lichen = Noise.Cells(scale: 48, output: CellOutput.F1).Levels(0f, 0.3f).Invert()
            .Multiply(Noise.Perlin(scale: 6, octaves: 3).Levels(0.55f, 0.7f));

        // Dark and light flecks of grit and a mid-sized mottle give the colour detail at every size,
        // not only in broad patches.
        var flecks = Noise.White().Blur(0.0005f).Levels(0.2f, 0.8f).Remap(0.8f, 1.12f);
        var stone = Noise.Perlin(scale: 3, octaves: 4)
            .GradientMap(Ramp.Of("#6d665c", "#837b6f", "#8e877b", "#6f6a62"))
            .Multiply(Noise.Perlin(scale: 30, octaves: 4).Remap(0.85f, 1.1f))
            .Multiply(Noise.Perlin(scale: 12, octaves: 3).Remap(0.88f, 1.08f))
            .Multiply(flecks)
            .Multiply(cracks.Remap(1f, 0.55f))
            .Multiply(spalls.Remap(1f, 1.12f));
        // The arrises the texture has, the sharp rims round the spalls, catch the light: a 1 cm bevel highlight on the
        // intact side of each rim. (The blocks' own arrises are geometry, and the sidecar's baked wear lightens them.)
        // The peaks of the height are rubbed 5% lighter.
        var arris = spalls.EdgeDetect(0.01f).Multiply(spalls.Levels(0.45f, 0.55f).Invert());
        stone = stone
            .Multiply(arris.Remap(1f, 1.1f))
            .Multiply(height.Levels(0.65f, 0.9f).Remap(1f, 1.05f));
        var lichenColor = Noise.Perlin(scale: 60, octaves: 2).GradientMap(Ramp.Of("#a3a28e", "#b9b49a"));
        var mossColor = Noise.Perlin(scale: 40, octaves: 3).GradientMap(Ramp.Of("#3d4a22", "#556429"));
        var color = stone.Blend(lichenColor, mask: lichen.Multiply(0.85f)).Blend(mossColor, mask: moss);

        return new Material
        {
            Width = 1f,
            Depth = 0.015f,
            Occlusion = 0.85f,
            Height = height,
            Color = color,
            // Worn smooth on the high spots, rough in the pits, the cracks, the lichen and the moss.
            Roughness = height.Invert().Remap(0.6f, 0.9f).Max(moss.Remap(0f, 0.95f)).Max(lichen.Remap(0f, 0.92f)),
        };
    }
}
