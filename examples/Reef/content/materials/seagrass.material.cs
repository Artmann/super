using Super.Materials;

/// <summary>
/// A seagrass blade, 50 cm along: green with fine veins running its length, and the odd pale fleck of the tiny
/// growths that settle on old leaves. The blade is one image across, so the veins run up the image.
/// </summary>
public sealed class Seagrass : MaterialGraph
{
    public override Material Build()
    {
        var veins = Noise.Directional(scale: 40, angle: 90f, stretch: 16f, octaves: 2, seed: 1);
        var tone = Noise.Perlin(scale: 3, octaves: 2, seed: 2);
        var flecks = Noise.Cells(scale: 30, output: CellOutput.F1, seed: 3).Levels(0f, 0.12f).Invert()
            .Multiply(Noise.Perlin(scale: 4, octaves: 2, seed: 4).Levels(0.55f, 0.7f));

        var color = tone.Multiply(0.6f).Add(veins.Multiply(0.4f)).GradientMap(Ramp.Of("#3b5520", "#55722b", "#6f8a36"))
            .Blend(Rgb.Of("#8a7a45"), mask: tone.Levels(0.65f, 0.8f), opacity: 0.6f)
            .Blend(Rgb.Of("#c9c6a6"), mask: flecks, opacity: 0.8f);

        return new Material
        {
            Width = 0.5f,
            Depth = 0.001f,
            Height = veins.Multiply(0.5f).Add(0.25f),
            Color = color,
            Roughness = tone.Remap(0.42f, 0.62f).Blend(Gray.Of(0.8f), mask: flecks),
        };
    }
}
