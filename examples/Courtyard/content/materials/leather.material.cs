using Super.Materials;

/// <summary>Dark brown leather with soft folds and creases over a pebbled grain, polished smoother and paler where it is worn.</summary>
public sealed class Leather : MaterialGraph
{
    public override Material Build()
    {
        // Low rounded bumps about 6 mm across over the 0.5 m, with creases between them, blurred soft.
        var grain = Noise.Cells(scale: 80, output: CellOutput.Edge)
            .Levels(0f, 0.35f, gamma: 1.6f)
            .Blur(0.0008f);
        var wear = Noise.Perlin(scale: 4, octaves: 5).HistogramScan(position: 0.35f, contrast: 0.3f);

        // Broad folds in the hide, and creases where it has bent: wandering cell edges, a few mm wide.
        var folds = Noise.Perlin(scale: 3, octaves: 3);
        var creases = Noise.Cells(scale: 6, output: CellOutput.Edge)
            .Warp(Noise.Perlin(scale: 5, octaves: 2), amount: 0.005f)
            .Levels(0f, 0.06f)
            .Invert()
            .Blur(0.003f);

        // The folds and creases make most of the height, so they read from across the yard; the grain
        // stays small and is pressed flatter where the leather is worn.
        var height = folds.Multiply(0.6f)
            .Subtract(creases.Multiply(0.3f))
            .Add(grain.Multiply(wear.Remap(0.15f, 0.06f)));

        var color = Noise.Perlin(scale: 12, octaves: 4)
            .GradientMap(Ramp.Of("#4a2a18", "#5a3420", "#663c24"))
            .Multiply(grain.Remap(0.88f, 1.03f))
            .Multiply(creases.Remap(1f, 0.45f))
            .Blend(Rgb.Of("#7e5232"), mask: wear, opacity: 0.5f);

        return new Material
        {
            Width = 0.5f,
            Depth = 0.005f,
            Height = height,
            Color = color,
            Roughness = wear.Remap(0.55f, 0.32f).Add(grain.Remap(-0.05f, 0.05f)).Add(creases.Remap(0f, 0.1f)),
        };
    }
}
