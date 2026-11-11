using Super.Materials;

/// <summary>
/// An old coat of limewash, off-white and chalky, with rain streaks of grime running down it. Flakes of it have come
/// away; there the opacity is 0, so with an alpha cutoff the coat has holes in it and the courtyard's own brick wall,
/// mortar and all, shows through them.
/// </summary>
public sealed class Whitewash : MaterialGraph
{
    public override Material Build()
    {
        // Brushed on thick and uneven, so the coat has soft lumps and fine grit.
        var lumps = Noise.Perlin(scale: 6, octaves: 5).Remap(0.35f, 0.65f);
        var grit = Noise.White().Blur(0.0006f).Remap(-0.03f, 0.03f);

        // Where the coat has flaked off. Limewash lets go in brittle flakes, so their edges are bitten and jagged:
        // the blotches are smeared along the slope of a finer noise, taking the most lost along each smear.
        var flaked = Noise.Perlin(scale: 4, octaves: 5, seed: 10)
            .HistogramScan(position: 0.2f, contrast: 0.98f)
            .SlopeBlur(Noise.Perlin(scale: 18, octaves: 4, seed: 11), amount: 0.015f, mode: SlopeBlurMode.Max)
            .Levels(0.45f, 0.55f);
        var coated = flaked.Invert();

        // The coat curls up a little at the edge of each flake, where it has started to lift.
        var lip = flaked.Distance(0.006f).Multiply(coated);
        var height = lumps.Multiply(0.4f).Add(0.55f).Add(grit).Add(lip.Multiply(0.08f));

        // Rain has carried dirt down the wall in long streaks, heavier in some places than others.
        var streaks = Noise.Directional(scale: 24, angle: 90f, stretch: 10f, octaves: 4)
            .Levels(0.45f, 0.85f)
            .Multiply(Noise.Perlin(scale: 3, octaves: 3).Levels(0.3f, 0.7f));
        var coat = Noise.Perlin(scale: 8, octaves: 4)
            .GradientMap(Ramp.Of("#a9a191", "#bdb5a5", "#c9c1b0", "#b3ab9b"))
            .Multiply(Noise.Perlin(scale: 40, octaves: 3).Remap(0.92f, 1.04f))
            // The lifting edge of each flake has caught a little dirt underneath it, a thin dark line.
            .Multiply(lip.Levels(0.6f, 1f).Remap(1f, 0.8f));
        var grime = Noise.Perlin(scale: 20, octaves: 3).GradientMap(Ramp.Of("#6f665a", "#857b6c"));

        return new Material
        {
            Width = 1f,
            Depth = 0.006f,
            Height = height,
            Color = coat.Blend(grime, mask: streaks.Multiply(0.6f)),
            // Chalky and dull, a little less so where hands and rain have polished it.
            Roughness = Noise.Perlin(scale: 16, octaves: 3).Remap(0.78f, 0.98f),
            // Solid where the coat is left and clear where it has flaked, with the flakes' jagged edges at 0.5.
            Opacity = coated,
        };
    }
}
