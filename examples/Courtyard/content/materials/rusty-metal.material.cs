using Super.Materials;

/// <summary>Steel plate with patches of flaking rust. The steel is metal and the rust is not.</summary>
public sealed class RustyMetal : MaterialGraph
{
    public override Material Build()
    {
        var rust = Noise.Perlin(scale: 4, octaves: 8).HistogramScan(position: 0.45f, contrast: 0.92f);
        var flakes = Noise.Perlin(scale: 64, octaves: 4);

        var height = rust.Multiply(0.4f)
            .Add(flakes.Multiply(rust).Multiply(0.3f))
            .Add(Noise.Perlin(scale: 128, octaves: 2).Remap(-0.02f, 0.02f));

        var steel = Noise.Directional(scale: 128, angle: 0f, stretch: 32f, octaves: 3)
            .GradientMap(Ramp.Of("#9a9a98", "#b4b4b0"));
        var rustColor = flakes.Multiply(0.6f).Add(Noise.Perlin(scale: 16, octaves: 4).Multiply(0.4f))
            .GradientMap(Ramp.Of("#4e2614", "#6e3418", "#8c4c26"));
        var color = steel.Blend(rustColor, mask: rust);

        return new Material
        {
            Width = 1f,
            Depth = 0.006f,
            Height = height,
            Color = color,
            Metallic = rust.Invert(),
            Roughness = rust.Remap(0.35f, 0.9f).Add(flakes.Remap(-0.06f, 0.06f)),
        };
    }
}
