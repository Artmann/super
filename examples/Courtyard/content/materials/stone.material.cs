using Super.Materials;

/// <summary>Weathered grey-brown stone with pits, fine grit and patches of moss, for the rough stones of the walls and the cobbles.</summary>
public sealed class Stone : MaterialGraph
{
    public override Material Build()
    {
        // Broad lumps and small pits, with a fine grain over them.
        var lumps = Noise.Perlin(scale: 4, octaves: 6).Remap(0.3f, 0.7f);
        var pits = Noise.Cells(scale: 18, output: CellOutput.F1).Levels(0f, 0.35f).Multiply(0.12f);
        var grain = Noise.White().Blur(0.0008f).Remap(-0.04f, 0.04f);
        var height = lumps.Add(grain).Subtract(Noise.Perlin(scale: 20, octaves: 3).Levels(0.55f, 0.75f).Multiply(0.1f)).Add(pits);

        // Moss sits in the low places.
        var moss = Noise.Perlin(scale: 5, octaves: 5).Levels(0.55f, 0.75f).Multiply(height.Invert().Levels(0.35f, 0.6f));

        var stone = Noise.Perlin(scale: 3, octaves: 4)
            .GradientMap(Ramp.Of("#6d665c", "#837b6f", "#8e877b", "#6f6a62"))
            .Multiply(Noise.Perlin(scale: 30, octaves: 4).Remap(0.85f, 1.1f));
        var mossColor = Noise.Perlin(scale: 40, octaves: 3).GradientMap(Ramp.Of("#3d4a22", "#556429"));
        var color = stone.Blend(mossColor, mask: moss);

        return new Material
        {
            Width = 1f,
            Depth = 0.015f,
            Height = height,
            Color = color,
            // Worn smooth on the high spots, rough in the pits and the moss.
            Roughness = height.Invert().Remap(0.6f, 0.9f).Max(moss.Remap(0f, 0.95f)),
        };
    }
}
