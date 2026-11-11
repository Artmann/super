using Super.Materials;

/// <summary>
/// Fine gravel kicked off a path, half a metre across, for laying as a decal along the path's edges and round the
/// barrel. 400 stones 2 to 8 mm across, in three greys, lie on a bed of coarse grit about 3 mm a grain, in a long spill.
/// It lies thick from about V 0.11 to 0.75, with a sharp edge towards V 0, the side the gravel came from, and thins out
/// over the next tenth of the image towards V 1. The grit thins out in drifts a whole grain at a time and
/// the stones a whole stone at a time, so the last of the spill is a scatter of single grains and stones and nothing
/// shows of the square it is drawn in.
/// </summary>
public sealed class Gravel : MaterialGraph
{
    /// <summary>How many stones the image holds.</summary>
    public const int Stones = 400;

    public override Material Build()
    {
        // Where the spill lies: an oval along U, its middle a little towards V 0, bent out of round and fuller on that
        // side. It is 1 in the middle and falls to 0 well before the image's edge.
        var oval = Pixel.Gray(p =>
        {
            var across = (p.Uv.X - 0.5f) / 0.43f;
            var down = (p.Uv.Y - 0.45f) / 0.39f;
            var inside = 1f - (across * across + down * down);
            return inside <= 0f ? 0f : inside * (1.15f - 0.5f * p.Uv.Y);
        });
        // Bending it throws a few wisps of its rim far out, so it is faded to nothing over the last eighth of the image
        // on every side. The oval reaches into that fade towards V 0 but not towards V 1, which gives the spill its
        // sharp edge on the side the gravel came from.
        var inside = Pixel.Gray(p =>
        {
            var border = MathF.Min(MathF.Min(p.Uv.X, 1f - p.Uv.X), MathF.Min(p.Uv.Y, 1f - p.Uv.Y));
            var t = Math.Clamp((border - 0.05f) / 0.08f, 0f, 1f);
            return t * t * (3f - 2f * t);
        });
        var spill = oval.Warp(Noise.Perlin(scale: 5, octaves: 3, seed: 1), amount: 0.012f).Levels(0f, 0.8f)
            .Multiply(inside);

        // The bed of grit: coarse grains about 3 mm across, each a cell with dark gaps round it, piled a little higher where the
        // spill is thick.
        var grains = Noise.White(seed: 2).Blur(0.002f);
        var grit = Noise.Cells(scale: 150, output: CellOutput.Id, seed: 2);
        var gaps = Noise.Cells(scale: 150, output: CellOutput.Edge, seed: 2).Levels(0f, 0.3f);
        var bed = spill.Multiply(0.1f).Add(gaps.Multiply(grit.Remap(0.03f, 0.08f)));

        // The stones, 2 to 8 mm across: discs bent out of round, softened at the rim so each is a low dome.
        var stone = Shape.Of(ShapeKind.Disc).Warp(Noise.Perlin(scale: 3, octaves: 2, seed: 3), amount: 0.02f);
        var stones = stone.Splatter(count: Stones, sizeMin: 0.004f, sizeMax: 0.016f, seed: 4);
        var domes = stones.Blur(0.0012f).Multiply(stones);
        var laid = Gray.HeightBlend(bed, domes.Multiply(0.7f).Add(stones.Multiply(0.2f)), offset: 0.5f, contrast: 0.95f);

        // Each stone one of three greys, a pale limestone, a blue-grey and a warm grey, and its own shade of it.
        var ids = stones.FloodFill(threshold: 0.5f);
        var kind = ids.RandomGray(seed: 5);
        var shade = ids.RandomGray(seed: 6).Remap(0.85f, 1.1f);
        var stoneColor = kind.GradientMap(Ramp.Of(
                (0f, "#918a7c"), (0.33f, "#918a7c"),
                (0.34f, "#55585c"), (0.66f, "#55585c"),
                (0.67f, "#7b7369"), (1f, "#7b7369")))
            .Multiply(shade);
        var gritColor = grit.GradientMap(Ramp.Of("#4a4136", "#61574a", "#7b6f5f")).Multiply(gaps.Remap(0.55f, 1f));
        var color = gritColor.Blend(stoneColor, mask: laid.Mask);

        // The grit lies thick in the middle of the spill and thins in drifts towards its edge. It thins a whole grain
        // at a time, so the mud shows between grains rather than through a smear. A stone is kept or left out whole
        // the same way, more of them left out further out.
        var drifts = Noise.Perlin(scale: 6, octaves: 3, seed: 8).Levels(0.25f, 0.65f).Remap(0.55f, 1f);
        var cover = spill.Levels(0.05f, 0.4f).Multiply(drifts);
        // A grain's own value, stirred so the grains kept aren't the darkest ones.
        var gritKept = Pixel.Gray(p =>
        {
            var draw = p.Gray(0) * 97.13f;
            return draw - MathF.Floor(draw) < p.Gray(1) ? 1f : 0f;
        }, grit, cover);
        var kept = Pixel.Gray(p => p.Gray(0) < p.Gray(1) ? 1f : 0f, ids.RandomGray(seed: 7), spill.Multiply(1.6f));
        var opacity = gritKept.Multiply(gaps.Remap(0.35f, 1f))
            .Max(laid.Mask.Multiply(kept).Multiply(spill.Levels(0f, 0.03f)));

        return new Material
        {
            Width = 0.5f,
            Depth = 0.004f,
            Occlusion = 0.9f,
            Height = laid.Height,
            Color = color,
            Roughness = laid.Mask.Remap(0.88f, 0.6f).Add(grains.Remap(-0.04f, 0.04f)),
            Opacity = opacity,
        };
    }
}
