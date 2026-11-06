using Super.Materials;

/// <summary>Old red bricks in pale mortar, each brick tilted and coloured on its own, with chipped edges.</summary>
public sealed class Bricks : MaterialGraph
{
    public override Material Build()
    {
        // 8 bricks of 37.5 cm across the 3 m, 26 courses of about 11.5 cm: big old bricks,
        // so they read at the distance the courtyard is seen from.
        var bricks = Tiles.Bricks(columns: 8, rows: 26, gap: 0.14f, bevel: 0.15f);
        var ids = bricks.FloodFill();

        // Each brick leans its own way: a ramp across and a ramp down, each scaled by a random amount from -1 to 1.
        var across = ids.ToGradient(0f).Subtract(0.5f).Multiply(ids.RandomGray(seed: 1).Remap(-1f, 1f));
        var down = ids.ToGradient(90f).Subtract(0.5f).Multiply(ids.RandomGray(seed: 2).Remap(-1f, 1f));
        var tilt = across.Add(down).Multiply(0.15f);

        var height = bricks
            .Multiply(ids.RandomGray(seed: 3).Remap(0.8f, 1f))
            .Add(tilt.Multiply(bricks))
            .SlopeBlur(Noise.Cells(scale: 24), amount: 0.004f, mode: SlopeBlurMode.Min)
            .Add(Noise.Perlin(scale: 48, octaves: 5).Remap(-0.06f, 0.06f));

        var mortarMask = bricks.Levels(0f, 0.25f).Invert();
        var brickColor = ids.RandomGray(seed: 4)
            .GradientMap(Ramp.Of("#7a3324", "#94452f", "#a65a3c", "#8a4a3a"))
            .Multiply(Noise.Perlin(scale: 64, octaves: 4).Remap(0.82f, 1.08f));
        var mortarColor = Noise.Perlin(scale: 96, octaves: 3).GradientMap(Ramp.Of("#9c968a", "#b5ae9f"));
        var color = brickColor.Blend(mortarColor, mask: mortarMask);

        return new Material
        {
            Width = 3f,
            Depth = 0.02f,
            Height = height,
            Color = color,
            Roughness = mortarMask.Remap(0.82f, 0.95f).Add(Noise.Perlin(scale: 32, octaves: 3).Remap(-0.06f, 0.06f)),
        };
    }
}
