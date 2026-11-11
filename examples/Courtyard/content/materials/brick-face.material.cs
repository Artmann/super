using Super.Materials;

/// <summary>
/// The face of one rubbed brick, with no mortar: the soft orange of the arch and the quoins,
/// with pores, grit and darker fired patches. The model gives each brick its own box, so the
/// joints are real gaps.
/// </summary>
public sealed class BrickFace : MaterialGraph
{
    public override Material Build()
    {
        // A gently uneven face, with small pores and fine grit.
        var pores = Noise.Cells(scale: 160, output: CellOutput.F1).Levels(0f, 0.22f);
        var height = Noise.Perlin(scale: 6, octaves: 5).Remap(0.4f, 0.6f)
            .Add(Noise.White().Blur(0.0006f).Remap(-0.03f, 0.03f))
            .Subtract(pores.Invert().Multiply(0.08f));

        // Patchy firing, flecks of grit and dark pores, so the face has detail close up. The orange is
        // greyed a little, to sit with the wall's bricks, and rubbed bricks are hard, so the face has a slight sheen.
        var color = Noise.Perlin(scale: 5, octaves: 5)
            .GradientMap(Ramp.Of("#91503b", "#a46048", "#b16f56", "#9e5e4a"))
            .Multiply(Noise.Perlin(scale: 40, octaves: 4).Remap(0.86f, 1.08f))
            .Multiply(Noise.White().Blur(0.0005f).Levels(0.2f, 0.8f).Remap(0.88f, 1.08f))
            .Multiply(pores.Remap(0.7f, 1f));

        return new Material
        {
            Width = 1f,
            Depth = 0.004f,
            Occlusion = 0.85f,
            Height = height,
            Color = color,
            Roughness = Noise.Perlin(scale: 24, octaves: 3).Remap(0.6f, 0.85f).Max(pores.Invert().Remap(0f, 0.96f)),
        };
    }
}
