using Super.Materials;

/// <summary>Weathered planks with growth rings and fine grain running along them.</summary>
public sealed class Wood : MaterialGraph
{
    public override Material Build()
    {
        // Six planks of about 16 cm, running left to right, with their ends staggered.
        var planks = Tiles.Bricks(columns: 1, rows: 6, gap: 0.03f, bevel: 0.08f, offset: 0.5f);
        var ids = planks.FloodFill();

        // Rings: a ramp repeated down the image, bent by noise stretched along the grain,
        // and shifted per plank so no two planks share their rings.
        var rings = Gradient.Linear(90f)
            .Transform(tile: 12)
            .Warp(Noise.Directional(scale: 6, angle: 0f, stretch: 6f, octaves: 2), amount: 0.006f)
            .DirectionalWarp(ids.RandomGray(seed: 1), amount: 0.4f, angle: 90f)
            .Curve((0f, 0f), (0.7f, 0.15f), (0.9f, 1f), (1f, 0.6f));
        var grain = Noise.Directional(scale: 96, angle: 0f, stretch: 16f, octaves: 4);

        var height = planks
            .Multiply(ids.RandomGray(seed: 2).Remap(0.9f, 1f))
            .Subtract(rings.Multiply(0.05f))
            .Subtract(grain.Multiply(0.08f));

        var color = rings.Multiply(0.6f).Add(grain.Multiply(0.4f))
            .GradientMap(Ramp.Of("#a87c52", "#8d6440", "#6e4a2c"))
            .Multiply(ids.RandomGray(seed: 3).Remap(0.85f, 1.05f));

        return new Material
        {
            Width = 1f,
            Depth = 0.01f,
            Height = height,
            Color = color,
            Roughness = grain.Remap(0.62f, 0.8f).Add(rings.Multiply(0.05f)),
        };
    }
}
