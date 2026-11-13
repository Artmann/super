using Super.Materials;

/// <summary>
/// Brain coral, 50 cm across: rounded ridges about 1.5 cm apart that wander and loop like a maze, with narrow dark
/// grooves between them.
/// </summary>
public sealed class BrainCoral : MaterialGraph
{
    public override Material Build()
    {
        // The ridges are contour lines of a smooth noise, which wander, fork and close into loops as a brain coral's do.
        var flow = Noise.Perlin(scale: 4, octaves: 3, seed: 1);
        var ridges = Pixel.Gray(p => MathF.Sqrt(0.5f + 0.5f * DMath.Sin(p.Gray(0) * 140f)), flow);
        // Fine teeth across each groove, and grit over everything.
        var teeth = Noise.Directional(scale: 160, angle: 0f, stretch: 6f, octaves: 1, seed: 2).Multiply(ridges.Invert());
        var grain = Noise.White(seed: 3).Blur(0.0006f).Remap(-0.03f, 0.03f);
        var height = ridges.Add(teeth.Multiply(0.15f)).Add(grain);

        var groove = ridges.Invert().Levels(0.4f, 0.9f);
        var tone = Noise.Perlin(scale: 3, octaves: 3, seed: 4);
        var color = tone.GradientMap(Ramp.Of("#b8a57a", "#cbbd8f", "#bfb586"))
            .Blend(Rgb.Of("#e2d6ac"), mask: ridges.Levels(0.9f, 1f), opacity: 0.5f)
            .Blend(tone.GradientMap(Ramp.Of("#6a6440", "#7a7248")), mask: groove);

        return new Material
        {
            Width = 0.5f,
            Depth = 0.012f,
            Occlusion = 0.6f,
            Height = height,
            Color = color,
            Roughness = Gray.Of(0.6f).Blend(Gray.Of(0.8f), mask: groove),
        };
    }
}
