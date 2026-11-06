using Super.Materials;

/// <summary>Wet brown mud after rain: churned lumps and footprints, grit, and glossy where water still lies in the hollows.</summary>
public sealed class Mud : MaterialGraph
{
    public override Material Build()
    {
        // Churned: broad swells, then sharper clods pushed up between prints.
        var swell = Noise.Perlin(scale: 3, octaves: 5).Remap(0.3f, 0.6f);
        var clods = Noise.Cells(scale: 14, output: CellOutput.F1)
            .Warp(Noise.Perlin(scale: 10, octaves: 3), amount: 0.01f)
            .Levels(0.1f, 0.8f)
            .Multiply(0.18f);
        var grit = Noise.White().Blur(0.001f).Remap(-0.03f, 0.03f);
        var ground = swell.Add(clods).Add(grit);

        var pebbles = Shape.Of(ShapeKind.Bell).Splatter(count: 90, sizeMin: 0.008f, sizeMax: 0.025f);
        var blend = Gray.HeightBlend(ground, pebbles.Levels(0f, 0.3f).Multiply(0.6f).Add(pebbles.Multiply(0.3f)), offset: 0.55f, contrast: 0.9f);

        // Water lies in the low places, so they're dark and glossy.
        var wet = blend.Height.Invert().Levels(0.45f, 0.62f).Multiply(Noise.Perlin(scale: 4, octaves: 3).Levels(0.35f, 0.6f));

        var mudColor = ground.Levels(0.2f, 0.7f).GradientMap(Ramp.Of("#3a2617", "#553a24", "#6b4b30"));
        var wetColor = mudColor.Multiply(Gray.Of(0.55f));
        var pebbleColor = pebbles.FloodFill(threshold: 0.05f).RandomGray().GradientMap(Ramp.Of("#5d554b", "#776d60"));
        var color = mudColor.Blend(wetColor, mask: wet).Blend(pebbleColor, mask: blend.Mask);

        return new Material
        {
            Width = 2f,
            Depth = 0.03f,
            Height = blend.Height,
            Color = color,
            Roughness = wet.Remap(0.85f, 0.12f).Add(Noise.Perlin(scale: 16, octaves: 3).Remap(-0.04f, 0.04f)),
        };
    }
}
