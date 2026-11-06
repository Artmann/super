using Super.Materials;

/// <summary>Dark brown leather with a pebbled grain, polished smoother and paler where it is worn.</summary>
public sealed class Leather : MaterialGraph
{
    public override Material Build()
    {
        // Low rounded bumps about 6 mm across over the 0.5 m, with creases between them, blurred soft.
        var grain = Noise.Cells(scale: 80, output: CellOutput.Edge)
            .Levels(0f, 0.35f, gamma: 1.6f)
            .Blur(0.0008f);
        var wear = Noise.Perlin(scale: 4, octaves: 5).HistogramScan(position: 0.35f, contrast: 0.3f);

        // Worn leather is pressed flatter, and the hide has broad soft folds.
        var height = grain.Multiply(wear.Remap(1f, 0.4f))
            .Add(Noise.Perlin(scale: 6, octaves: 4).Remap(-0.2f, 0.2f));

        var color = Noise.Perlin(scale: 12, octaves: 4)
            .GradientMap(Ramp.Of("#4a2a18", "#5a3420", "#663c24"))
            .Multiply(grain.Remap(0.8f, 1.05f))
            .Blend(Rgb.Of("#7e5232"), mask: wear, opacity: 0.5f);

        return new Material
        {
            Width = 0.5f,
            Depth = 0.002f,
            Height = height,
            Color = color,
            Roughness = wear.Remap(0.55f, 0.32f).Add(grain.Remap(-0.06f, 0.06f)),
        };
    }
}
