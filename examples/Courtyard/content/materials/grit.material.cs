using Super.Materials;

/// <summary>
/// Fine grain for laying over stone and brick as a detail graph: sand-sized grit, small pits and faint scratches, a
/// quarter of a metre across, so a surface still has texture when the camera is a step away from it.
/// </summary>
public sealed class Grit : MaterialGraph
{
    public override Material Build()
    {
        var grains = Noise.White().Blur(0.0015f).Levels(0.25f, 0.75f);
        var pits = Noise.Cells(scale: 90, output: CellOutput.F1).Levels(0f, 0.18f);
        var mottle = Noise.Perlin(scale: 16, octaves: 3);

        var height = grains.Multiply(0.5f).Add(mottle.Multiply(0.3f)).Subtract(pits.Invert().Multiply(0.4f));
        // Only its brightness against its average counts, so a grey from about 40 to 55 per cent.
        var color = mottle.Remap(0.44f, 0.52f).Add(grains.Remap(-0.03f, 0.03f)).Subtract(pits.Invert().Multiply(0.06f))
            .GradientMap(Ramp.Of("#000000", "#ffffff"));

        return new Material
        {
            Width = 0.25f,
            Depth = 0.002f,
            Height = height,
            Color = color,
            // Only the normal and colour are laid over the main graph, but a graph cooked by itself needs a roughness.
            Roughness = mottle.Remap(0.7f, 0.9f),
        };
    }
}
