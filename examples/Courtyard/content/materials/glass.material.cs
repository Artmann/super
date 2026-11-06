using Super.Materials;

/// <summary>Old window glass: faintly green, rippled, and dirty with dust, rain streaks and specks.</summary>
public sealed class Glass : MaterialGraph
{
    public override Material Build()
    {
        // Old glass was drawn by hand, so it has gentle ripples running across it that bend
        // what it reflects.
        var ripples = Noise.Directional(scale: 10, angle: 0f, stretch: 4f, octaves: 2);

        // Grime: broad dusty patches, rain that ran down and dried, and specks of dirt.
        var dust = Noise.Perlin(scale: 3, octaves: 5).Levels(0.55f, 0.85f);
        var streaks = Noise.Directional(scale: 24, angle: 90f, stretch: 8f, octaves: 3)
            .Levels(0.55f, 0.8f)
            .Multiply(Noise.Perlin(scale: 4, octaves: 2).Levels(0.4f, 0.7f));
        var specks = Noise.White().Blur(0.0015f).Levels(0.6f, 0.64f);
        var grime = dust.Multiply(0.7f).Max(streaks.Multiply(0.6f)).Max(specks);

        return new Material
        {
            Width = 1f,
            Depth = 0.001f,
            Height = ripples.Add(grime.Multiply(0.15f)),
            // Clean glass is a faint green tint over the dark room; the grime is a dry brown-grey.
            Color = grime.GradientMap(Ramp.Of("#2e3a35", "#7d7464")),
            // Clean glass is clear and smooth, and the grime clouds it and dulls the reflection.
            Opacity = grime.Remap(0.05f, 0.65f),
            Roughness = grime.Remap(0.05f, 0.6f),
        };
    }
}
