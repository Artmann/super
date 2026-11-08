using Super.Materials;

/// <summary>Split walling stone with its bedding, a few long cracks along it, crusts of lichen and moss in the hollows, for the dry-stone wall. The cook tints each stone's slot its own colour.</summary>
public sealed class DryStone : MaterialGraph
{
    public override Material Build()
    {
        // The stone was laid down in beds, and a split face shows them as faint bands running
        // across it, some standing a little proud of the others where they weather harder.
        var bedding = Noise.Directional(scale: 12, angle: 0f, stretch: 10f, octaves: 4);
        var lumps = Noise.Perlin(scale: 3, octaves: 6).Remap(0.3f, 0.7f);
        var grain = Noise.White().Blur(0.0008f).Remap(-0.03f, 0.03f);
        var height = lumps
            .Add(bedding.Remap(-0.07f, 0.07f))
            .Add(grain)
            .Add(Noise.Cells(scale: 16, output: CellOutput.F1).Levels(0f, 0.35f).Multiply(0.08f));

        // A few long cracks that follow the bedding: thin lines where a stretched noise crosses
        // its middle, only in some places.
        var seamNoise = Noise.Directional(scale: 6, angle: 0f, stretch: 6f, octaves: 1, seed: 3);
        var seams = seamNoise.Levels(0.49f, 0.5f).Min(seamNoise.Levels(0.51f, 0.5f))
            .Multiply(Noise.Perlin(scale: 3, octaves: 2, seed: 4).Levels(0.58f, 0.68f));
        height = height.Subtract(seams.Multiply(0.08f));

        // Flakes split off along the beds, leaving shallow flat scars with a sharp rim.
        var flakes = Noise.Directional(scale: 10, angle: 0f, stretch: 4f, octaves: 4, seed: 5).Levels(0.66f, 0.69f);
        height = height.Subtract(flakes.Multiply(0.14f));

        // Moss gathers in the hollows and the cracks, where the damp stays.
        var hollows = height.Invert().Levels(0.4f, 0.62f);
        var moss = Noise.Perlin(scale: 6, octaves: 5).Levels(0.5f, 0.7f).Multiply(hollows).Max(seams.Multiply(0.7f));

        // Crusts of lichen: round spots that grow into each other in patches, pale grey-green
        // mostly, and a few spots of ochre.
        var crust = Noise.Cells(scale: 48, output: CellOutput.F1).Levels(0.15f, 0.4f).Invert()
            .Multiply(Noise.Perlin(scale: 4, octaves: 4, seed: 6).Levels(0.6f, 0.72f));
        var ochre = Noise.Cells(scale: 64, output: CellOutput.F1, seed: 7).Levels(0f, 0.3f).Invert()
            .Multiply(Noise.Perlin(scale: 8, octaves: 3, seed: 8).Levels(0.68f, 0.78f));

        // A grey-brown stone, banded light and dark along its beds, with flecks of grit and
        // paler where flakes came off.
        var flecks = Noise.White().Blur(0.0005f).Levels(0.2f, 0.8f).Remap(0.84f, 1.1f);
        var stone = Noise.Perlin(scale: 2, octaves: 4)
            .GradientMap(Ramp.Of("#6a645b", "#7d766b", "#8a8478", "#76716a"))
            .Multiply(bedding.Remap(0.86f, 1.1f))
            .Multiply(Noise.Perlin(scale: 24, octaves: 4).Remap(0.88f, 1.08f))
            .Multiply(flecks)
            .Multiply(seams.Remap(1f, 0.72f))
            .Multiply(flakes.Remap(1f, 1.14f));
        var crustColor = Noise.Perlin(scale: 40, octaves: 2).GradientMap(Ramp.Of("#9c9d86", "#b5b39a"));
        var ochreColor = Noise.Perlin(scale: 40, octaves: 2, seed: 9).GradientMap(Ramp.Of("#9a7a42", "#b08d4c"));
        var mossColor = Noise.Perlin(scale: 40, octaves: 3).GradientMap(Ramp.Of("#36421f", "#4f5d27"));
        var color = stone
            .Blend(crustColor, mask: crust.Multiply(0.8f))
            .Blend(ochreColor, mask: ochre.Multiply(0.75f))
            .Blend(mossColor, mask: moss);

        return new Material
        {
            Width = 1f,
            Depth = 0.015f,
            Occlusion = 0.85f,
            Height = height,
            Color = color,
            // Smoothish on the high beds, rough in the hollows and under lichen and moss.
            Roughness = height.Invert().Remap(0.62f, 0.9f).Max(moss.Remap(0f, 0.95f)).Max(crust.Remap(0f, 0.92f)),
        };
    }
}
