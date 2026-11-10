using System.Numerics;
using Super.Materials;

/// <summary>
/// An old coat of limewash, off-white and chalky, with rain streaks of grime running down it. Flakes of it have come
/// away, showing the brick beneath, and rain has washed lime from their edges down over the bare brick.
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

        // The coat stands 3 mm proud of the brick, and curls up a little at the edge of each flake, where it has
        // started to lift.
        var lip = flaked.Distance(0.006f).Multiply(coated);
        var coatHeight = lumps.Multiply(0.4f).Add(0.55f).Add(grit).Add(lip.Multiply(0.08f));
        var brickHeight = lumps.Multiply(0.4f).Add(0.05f).Add(grit.Multiply(1.5f));
        var height = brickHeight.Blend(coatHeight, mask: coated);

        // The brick beneath, coloured as the courtyard's brick faces are, but still filmed with lime in its pores.
        var pores = Noise.Cells(scale: 160, output: CellOutput.F1).Levels(0f, 0.22f);
        var brick = Noise.Perlin(scale: 5, octaves: 5).GradientMap(Ramp.Of("#91503b", "#a46048", "#b16f56", "#9e5e4a"))
            .Multiply(Noise.Perlin(scale: 40, octaves: 4).Remap(0.86f, 1.08f))
            .Multiply(Noise.White().Blur(0.0005f).Levels(0.2f, 0.8f).Remap(0.88f, 1.08f))
            .Multiply(pores.Remap(0.7f, 1f))
            .Blend(Rgb.Of("#c8bfae"), mask: pores.Invert().Multiply(0.35f));

        // Rain running down the wall picks lime up from the bottom edge of the coat above and leaves it in streaks
        // down the bare brick, strongest just under the edge and fading out a few centimetres down.
        var streakNoise = Noise.Directional(scale: 70, angle: 90f, stretch: 10f, octaves: 3, seed: 12).Levels(0.4f, 0.8f);
        var runoff = Pixel.Gray(p =>
        {
            if (p.Gray(0) < 0.5f)
            {
                return 0f;
            }

            // Look up the image, which is up the wall, for the nearest coat.
            for (int i = 1; i <= 24; i++)
            {
                float up = i * 0.0025f;
                if (p.SampleGray(0, new Vector2(p.Uv.X, p.Uv.Y - up)) < 0.5f)
                {
                    float fade = 1f - (float)(i - 1) / 24f;
                    return fade * fade * p.Gray(1);
                }
            }

            return 0f;
        }, flaked, streakNoise);

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
        var color = brick
            .Blend(Rgb.Of("#bfb6a4"), mask: runoff, opacity: 0.75f)
            .Blend(coat, mask: coated)
            .Blend(grime, mask: streaks.Multiply(0.6f));

        return new Material
        {
            Width = 1f,
            Depth = 0.006f,
            Height = height,
            Color = color,
            // Chalky and dull, a little less so where hands and rain have polished it; the bare brick is rougher still.
            Roughness = Noise.Perlin(scale: 16, octaves: 3).Remap(0.78f, 0.98f)
                .Blend(Noise.Perlin(scale: 30, octaves: 3, seed: 13).Remap(0.86f, 0.95f), mask: flaked),
        };
    }
}
