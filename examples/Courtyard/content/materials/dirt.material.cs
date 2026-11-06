using Super.Materials;

/// <summary>Packed brown dirt with grit, scattered pebbles and dry cracks.</summary>
public sealed class Dirt : MaterialGraph
{
    public override Material Build()
    {
        var ground = Noise.Perlin(scale: 6, octaves: 7).Remap(0.2f, 0.45f)
            .Add(Noise.Perlin(scale: 24, octaves: 4).Remap(-0.08f, 0.08f))
            .Add(Noise.White().Blur(0.001f).Remap(-0.05f, 0.05f));

        // Cracks: thin lines on the cell borders, wobbled so they aren't straight.
        var cracks = Noise.Cells(scale: 5, output: CellOutput.Edge)
            .Warp(Noise.Perlin(scale: 8, octaves: 2), amount: 0.003f)
            .Levels(0f, 0.04f)
            .Invert();
        ground = ground.Subtract(cracks.Multiply(0.25f));

        // Pebbles 3 to 8 cm across, domed, lying on the dirt. The bell is lifted steeply at its rim so
        // nearly all of its stamp stands above the ground, not just the tip.
        var pebbles = Shape.Of(ShapeKind.Bell).Splatter(count: 160, sizeMin: 0.015f, sizeMax: 0.04f);
        var stones = pebbles.Levels(0f, 0.3f).Multiply(0.55f).Add(pebbles.Multiply(0.35f));
        var blend = Gray.HeightBlend(ground, stones, offset: 0.5f, contrast: 0.9f);

        var dirtColor = ground.Levels(0.05f, 0.55f).GradientMap(Ramp.Of("#4a3826", "#6b5238", "#7c6446"));
        var pebbleColor = pebbles.FloodFill(threshold: 0.05f).RandomGray().GradientMap(Ramp.Of("#625d55", "#7a7266", "#57534d"));
        var color = dirtColor.Blend(pebbleColor, mask: blend.Mask);

        return new Material
        {
            Width = 2f,
            Depth = 0.04f,
            Height = blend.Height,
            Color = color,
            Roughness = blend.Mask.Remap(0.95f, 0.75f).Add(Noise.Perlin(scale: 12, octaves: 3).Remap(-0.04f, 0.04f)),
        };
    }
}
