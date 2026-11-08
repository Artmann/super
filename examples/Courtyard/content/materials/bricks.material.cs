using Super.Materials;

/// <summary>Old red bricks in grey lime mortar, each brick tilted, coloured and worn on its own, with chipped edges.</summary>
public sealed class Bricks : MaterialGraph
{
    public override Material Build()
    {
        // 8 bricks of 43.75 cm across the 3.5 m, 12 courses of about 29 cm: big old bricks,
        // so they read at the distance the courtyard is seen from.
        var bricks = Tiles.Bricks(columns: 8, rows: 12, gap: 0.075f, bevel: 0.08f);
        var ids = bricks.FloodFill();

        // Each brick leans its own way: a ramp across and a ramp down, each scaled by a random amount from -1 to 1.
        var across = ids.ToGradient(0f).Subtract(0.5f).Multiply(ids.RandomGray(seed: 1).Remap(-1f, 1f));
        var down = ids.ToGradient(90f).Subtract(0.5f).Multiply(ids.RandomGray(seed: 2).Remap(-1f, 1f));
        var tilt = across.Add(down).Multiply(0.15f);

        // Bricks from one kiln still differ: some came out with a hard, smooth skin and others
        // soft and pitted. 0 is the smoothest, 1 the roughest.
        var wear = ids.RandomGray(seed: 5).Levels(0.1f, 0.9f);

        // The faces undulate gently, more on the soft bricks, rather than every face being
        // crumpled the same way.
        var undulation = Noise.Perlin(scale: 24, octaves: 3).Remap(-0.03f, 0.03f).Multiply(wear.Remap(0.2f, 1f));
        var height = bricks
            .Multiply(ids.RandomGray(seed: 3).Remap(0.8f, 1f))
            .Add(tilt.Multiply(bricks))
            .SlopeBlur(Noise.Cells(scale: 24), amount: 0.004f, mode: SlopeBlurMode.Min)
            .Add(undulation);

        // Small pores and fine grit in the brick faces and the mortar, so they hold detail close
        // up. The soft bricks are pitted far more than the hard ones.
        var pores = Noise.Cells(scale: 520, output: CellOutput.F1).Levels(0f, 0.22f);
        var grit = Noise.White().Blur(0.00025f).Levels(0.2f, 0.8f);
        var pitting = wear.Remap(0.25f, 1f);
        height = height.Add(grit.Remap(-0.012f, 0.012f)).Subtract(pores.Invert().Multiply(pitting).Multiply(0.05f));

        // Spalls: shallow scars a few centimetres across where the fired skin has flaked off the
        // face, showing paler brick under it. Only on the bricks, not the mortar.
        var spalls = Noise.Perlin(scale: 40, octaves: 4).Levels(0.64f, 0.68f).Multiply(bricks.Levels(0.3f, 0.6f));
        height = height.Subtract(spalls.Multiply(0.25f));

        var mortarMask = bricks.Levels(0f, 0.25f).Invert();
        // A wide spread from brick to brick: a few dark, overfired clinkers, deep reds, the
        // usual orange-red and some pale, underfired ones.
        var brickColor = ids.RandomGray(seed: 4)
            .GradientMap(Ramp.Of("#552c26", "#723024", "#8a3a28", "#9a462f", "#a45438", "#a9634a"))
            .Multiply(Noise.Perlin(scale: 24, octaves: 3).Remap(0.88f, 1.06f))
            .Multiply(Noise.Perlin(scale: 200, octaves: 3).Remap(0.92f, 1.05f))
            .Multiply(grit.Remap(0.92f, 1.05f))
            .Multiply(pores.Invert().Multiply(pitting).Multiply(0.28f).Invert())
            .Multiply(spalls.Remap(1f, 1.18f));
        // Old lime mortar, grey and dirty rather than clean, with patches where it was
        // repointed paler, and darker where dirt sits in the joints.
        var mortarColor = Noise.Perlin(scale: 12, octaves: 4).GradientMap(Ramp.Of("#6f695f", "#857e71", "#9e9789"))
            .Multiply(grit.Remap(0.86f, 1.08f))
            .Multiply(pores.Remap(0.8f, 1f));
        var color = brickColor.Blend(mortarColor, mask: mortarMask);

        // The hard bricks have a slight sheen; the soft ones and the mortar are dull.
        var brickRoughness = wear.Remap(0.6f, 0.9f).Add(Noise.Perlin(scale: 32, octaves: 3).Remap(-0.05f, 0.05f));

        return new Material
        {
            Width = 3.5f,
            Depth = 0.02f,
            Occlusion = 0.85f,
            Height = height,
            Color = color,
            Roughness = brickRoughness.Blend(Noise.Perlin(scale: 32, octaves: 3).Remap(0.9f, 0.97f), mask: mortarMask),
        };
    }
}
