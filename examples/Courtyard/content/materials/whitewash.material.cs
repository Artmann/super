using Super.Materials;

/// <summary>An old coat of limewash, off-white and chalky, with rain streaks of grime running down it.</summary>
public sealed class Whitewash : MaterialGraph
{
    public override Material Build()
    {
        // Brushed on thick and uneven, so the coat has soft lumps and fine grit.
        var lumps = Noise.Perlin(scale: 6, octaves: 5).Remap(0.35f, 0.65f);
        var grit = Noise.White().Blur(0.0006f).Remap(-0.03f, 0.03f);
        var height = lumps.Add(grit);

        // Rain has carried dirt down the wall in long streaks, heavier in some places than others.
        var streaks = Noise.Directional(scale: 24, angle: 90f, stretch: 10f, octaves: 4)
            .Levels(0.45f, 0.85f)
            .Multiply(Noise.Perlin(scale: 3, octaves: 3).Levels(0.3f, 0.7f));
        var coat = Noise.Perlin(scale: 8, octaves: 4)
            .GradientMap(Ramp.Of("#a9a191", "#bdb5a5", "#c9c1b0", "#b3ab9b"))
            .Multiply(Noise.Perlin(scale: 40, octaves: 3).Remap(0.92f, 1.04f));
        var grime = Noise.Perlin(scale: 20, octaves: 3).GradientMap(Ramp.Of("#6f665a", "#857b6c"));
        var color = coat.Blend(grime, mask: streaks.Multiply(0.6f));

        return new Material
        {
            Width = 1f,
            Depth = 0.004f,
            Height = height,
            Color = color,
            // Chalky and dull, a little less so where hands and rain have polished it.
            Roughness = Noise.Perlin(scale: 16, octaves: 3).Remap(0.78f, 0.98f),
        };
    }
}
