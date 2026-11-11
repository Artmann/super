using Super.Materials;

/// <summary>
/// Old forged hoop iron: dark, mostly crusted with rust, with bare metal only where it has rubbed, and rust bleeding down
/// it in streaks.
/// </summary>
/// <remarks>
/// The barrel's hoops run their U round the barrel and their V up across the band, so down the image is down the hoop.
/// </remarks>
public sealed class HoopIron : MaterialGraph
{
    public override Material Build()
    {
        // Most of it is rust. Bare iron shows in small worn patches.
        var crust = Noise.Perlin(scale: 12, octaves: 6).HistogramScan(position: 0.75f, contrast: 0.85f);

        // Rain runs down the band and carries rust with it in streaks, so even the bare patches are crossed by runs.
        var runs = Noise.Directional(scale: 48, angle: 90f, stretch: 10f, octaves: 3).Levels(0.6f, 0.66f)
            .Multiply(Noise.Perlin(scale: 6, octaves: 2).Levels(0.3f, 0.6f));
        var rust = crust.Max(runs);

        var scale = Noise.Cells(scale: 60, output: CellOutput.F1).Levels(0f, 0.6f);
        var height = rust.Multiply(0.5f)
            .Add(scale.Multiply(rust).Multiply(0.3f))
            .Add(Noise.White().Blur(0.001f).Remap(-0.05f, 0.05f));

        var iron = Noise.Perlin(scale: 40, octaves: 3).GradientMap(Ramp.Of("#4a4846", "#5c5955"));
        var rustColor = Noise.Perlin(scale: 30, octaves: 4).Multiply(0.6f).Add(scale.Multiply(0.4f))
            .GradientMap(Ramp.Of("#3a2418", "#4f2e1a", "#6a3d22"));
        // The runs are fresher rust, a brighter orange-brown than the old crust.
        var runColor = Noise.Perlin(scale: 24, octaves: 3).GradientMap(Ramp.Of("#5e3218", "#7a4424"));
        var color = iron.Blend(rustColor, mask: crust).Blend(runColor, mask: runs.Multiply(0.8f));

        return new Material
        {
            Width = 0.5f,
            Depth = 0.003f,
            Height = height,
            Color = color,
            Metallic = rust.Invert(),
            // Bare iron is smooth, 0.35; rust is dry and rough, 0.9.
            Roughness = rust.Remap(0.35f, 0.9f).Add(scale.Remap(-0.03f, 0.03f)),
        };
    }
}
