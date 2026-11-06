using Super.Materials;

/// <summary>Irregular grey flagstones with grit in the joints.</summary>
public sealed class Flagstones : MaterialGraph
{
    public override Material Build()
    {
        // About 5 stones across the 2 m. The cell edge is 0 on the joints.
        var edge = Noise.Cells(scale: 5, output: CellOutput.Edge);
        var stones = edge
            .HistogramScan(position: 0.93f, contrast: 0.94f)
            .Warp(Noise.Perlin(scale: 6, octaves: 2), amount: 0.003f);
        var ids = stones.FloodFill(threshold: 0.5f);

        var stoneHeight = stones
            .Blur(0.002f)
            .Multiply(ids.RandomGray(seed: 1).Remap(0.75f, 0.95f))
            .Add(Noise.Perlin(scale: 24, octaves: 6).Remap(-0.1f, 0.1f).Multiply(stones));

        var grit = Noise.White().Blur(0.0015f).Remap(0.1f, 0.4f).Add(Noise.Perlin(scale: 32, octaves: 3).Remap(0f, 0.1f));
        var blend = Gray.HeightBlend(stoneHeight, grit, offset: 0.5f, contrast: 0.9f);

        var stoneColor = ids.RandomGray(seed: 2)
            .GradientMap(Ramp.Of("#6c6962", "#7b776e", "#878278", "#72767a"))
            .Multiply(Noise.Perlin(scale: 40, octaves: 5).Remap(0.85f, 1.1f));
        var gritColor = Noise.White().GradientMap(Ramp.Of("#4e463c", "#6e6455"));
        var color = stoneColor.Blend(gritColor, mask: blend.Mask);

        return new Material
        {
            Width = 2f,
            Depth = 0.04f,
            Height = blend.Height,
            Color = color,
            Roughness = blend.Mask.Remap(0.7f, 0.95f).Add(Noise.Perlin(scale: 16, octaves: 4).Remap(-0.08f, 0.08f)),
        };
    }
}
