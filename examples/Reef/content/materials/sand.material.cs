using Super.Materials;

/// <summary>
/// Pale reef sand, 6 m across, combed by the swell into ripples about 30 cm apart. Each ripple rises gently on the side
/// the water comes from and drops steeply on the other, and the crests bend, fork and fade out in places. Broken shell
/// lies scattered over it, and finer dark grit and bits of weed gather in the troughs.
/// </summary>
public sealed class Sand : MaterialGraph
{
    /// <summary>Ripples across the image. A whole number, so they tile.</summary>
    private const int Ripples = 20;

    public override Material Build()
    {
        var (bent, strength, ground) = Ground();

        // Broken shell, 1 to 4 cm, lying flat on the sand.
        var flake = Shape.Of(ShapeKind.Disc).Warp(Noise.Perlin(scale: 3, octaves: 2, seed: 6), amount: 0.03f);
        var shells = flake.Splatter(count: 900, sizeMin: 0.002f, sizeMax: 0.007f, seed: 7);
        var height = ground.Blend(ground.Add(0.05f), mask: shells);

        // The troughs are a little darker and greener, where grit and fine weed settle.
        var trough = bent.Invert().Levels(0.55f, 1f).Multiply(strength);
        var tone = Noise.Perlin(scale: 5, octaves: 4, seed: 8).Multiply(0.6f).Add(ground.Multiply(0.4f));
        var sand = tone.GradientMap(Ramp.Of("#b29c74", "#c9b48b", "#dccaa2", "#e8dabb"));
        var silt = Noise.White(seed: 9).Blur(0.0006f).GradientMap(Ramp.Of("#6f6a4e", "#8f8763"));
        var specks = Noise.White(seed: 10).Levels(0.93f, 0.97f);
        var shellColor = shells.FloodFill(threshold: 0.5f).RandomGray(seed: 11)
            .GradientMap(Ramp.Of("#e4d6c4", "#f3ece0", "#d9b9a4", "#f7f3ea"));

        var color = sand
            .Blend(silt, mask: trough, opacity: 0.25f)
            .Blend(Rgb.Of("#5a5546"), mask: specks, opacity: 0.6f)
            .Blend(shellColor, mask: shells);

        return new Material
        {
            Width = 6f,
            Depth = 0.06f,
            Occlusion = 0.7f,
            Height = height,
            Color = color,
            Roughness = Gray.Of(0.85f).Blend(Gray.Of(0.55f), mask: shells),
        };
    }

    /// <summary>The bare rippled sand: the bent ripples, how strong they are in each place, and the height of both.</summary>
    private static (Gray Bent, Gray Strength, Gray Ground) Ground()
    {
        // One ripple: a long slope up to the crest over three quarters of its width, then a short steep fall, softened
        // at the top and in the trough.
        var ripple = Pixel.Gray(p =>
        {
            var phase = p.Uv.X * Ripples;
            var f = phase - MathF.Floor(phase);
            var h = f < 0.72f ? f / 0.72f : (1f - f) / 0.28f;
            return h * h * (3f - 2f * h);
        });
        // The crests wander and bend, and broad patches lie flatter where the swell has smoothed them out.
        var bent = ripple
            .Warp(Noise.Perlin(scale: 2, octaves: 1, seed: 1), amount: 0.006f)
            .Warp(Noise.Perlin(scale: 6, octaves: 1, seed: 2), amount: 0.0012f);
        var strength = Noise.Perlin(scale: 4, octaves: 3, seed: 3).Levels(0.25f, 0.7f, outLow: 0.25f);
        var swell = Noise.Perlin(scale: 2, octaves: 3, seed: 4);
        var grain = Noise.White(seed: 5).Blur(0.0004f).Remap(-0.02f, 0.02f);
        var ground = bent.Multiply(strength).Multiply(0.7f).Add(swell.Multiply(0.3f)).Add(grain);
        return (bent, strength, ground);
    }
}
