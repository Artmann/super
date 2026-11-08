using Super.Materials;

/// <summary>Old forged hoop iron: dark, mostly crusted with rust, with bare metal only where it has rubbed.</summary>
public sealed class HoopIron : MaterialGraph
{
    public override Material Build()
    {
        // Most of it is rust. Bare iron shows in small worn patches.
        var rust = Noise.Perlin(scale: 12, octaves: 6).HistogramScan(position: 0.75f, contrast: 0.85f);
        var scale = Noise.Cells(scale: 60, output: CellOutput.F1).Levels(0f, 0.6f);
        var height = rust.Multiply(0.5f)
            .Add(scale.Multiply(rust).Multiply(0.3f))
            .Add(Noise.White().Blur(0.001f).Remap(-0.05f, 0.05f));

        var iron = Noise.Perlin(scale: 40, octaves: 3).GradientMap(Ramp.Of("#4a4846", "#5c5955"));
        var rustColor = Noise.Perlin(scale: 30, octaves: 4).Multiply(0.6f).Add(scale.Multiply(0.4f))
            .GradientMap(Ramp.Of("#3a2418", "#4f2e1a", "#6a3d22"));
        var color = iron.Blend(rustColor, mask: rust);

        return new Material
        {
            Width = 0.5f,
            Depth = 0.003f,
            Height = height,
            Color = color,
            Metallic = rust.Invert(),
            Roughness = rust.Remap(0.45f, 0.92f).Add(scale.Remap(-0.04f, 0.04f)),
        };
    }
}
