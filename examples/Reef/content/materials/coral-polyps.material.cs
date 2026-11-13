using Super.Materials;

/// <summary>
/// The skin of a branching or plate coral, 15 cm across: packed little cups a few millimetres wide, each a raised rim
/// round a dark pit. Pale, so a tint on the draw gives each colony its colour.
/// </summary>
public sealed class CoralPolyps : MaterialGraph
{
    public override Material Build()
    {
        var cells = Noise.Cells(scale: 18, output: CellOutput.F1, jitter: 0.8f, seed: 1);
        // F1 is 0 at a cup's middle and about 0.5 at the edge between cups.
        var cups = Pixel.Gray(p =>
        {
            float d = p.Gray(0);
            float rim = DMath.Exp(-((d - 0.24f) / 0.09f) * ((d - 0.24f) / 0.09f));
            float pit = d < 0.14f ? 0.35f : 0f;
            return 0.35f + 0.65f * rim - pit;
        }, cells);
        var lumps = Noise.Perlin(scale: 3, octaves: 3, seed: 2);
        var height = cups.Multiply(0.7f).Add(lumps.Multiply(0.3f));

        var pit = cells.Levels(0.08f, 0.18f).Invert();
        var color = lumps.GradientMap(Ramp.Of("#ddd0b8", "#ece2cf", "#f4eee2"))
            .Blend(Rgb.Of("#fbf7ee"), mask: cups.Levels(0.8f, 1f), opacity: 0.6f)
            .Blend(Rgb.Of("#8f8068"), mask: pit, opacity: 0.8f);

        return new Material
        {
            Width = 0.15f,
            Depth = 0.004f,
            Occlusion = 0.8f,
            Height = height,
            Color = color,
            Roughness = Gray.Of(0.55f).Blend(Gray.Of(0.85f), mask: pit),
        };
    }
}
