using System.Numerics;
using Super.Materials;

/// <summary>
/// Coarse sacking: thick, uneven jute threads in a loose plain weave, with dark gaps between them, a patch of older
/// sacking sewn over a hole, a dark wet stain and a dusting of flour, loose threads lying on it, and grimy towards the
/// bottom where it has stood on the ground.
/// </summary>
public sealed class Burlap : MaterialGraph
{
    // 96 threads each way across the 0.5 m, about 5 mm apart.
    private const float Threads = 96f;

    // The patch: its middle and its width, as fractions of the image. The model's sacks have their UVs laid from their
    // foot, so 0.56 down the image is a little below the middle of a sack.
    private static readonly Vector2 PatchCentre = new(0.3f, 0.56f);
    private const float PatchSize = 0.24f;

    public override Material Build()
    {
        // Jute is lumpy, so each thread swells and thins along its length: noise stretched along it, one cell per
        // thread across.
        var warpSlub = Noise.Directional(scale: 96, angle: 90f, stretch: 12f, octaves: 2, seed: 1);
        var weftSlub = Noise.Directional(scale: 96, angle: 0f, stretch: 12f, octaves: 2, seed: 2);
        var weave = Pixel.Gray(p => Weave(p.Uv * Threads, p.Gray(0), p.Gray(1)), warpSlub, weftSlub);

        // Loose fibres standing off the threads, and the cloth's own sag and creases.
        var fuzz = Noise.White(seed: 3).Blur(0.0006f).Levels(0.45f, 0.75f);
        var creases = Noise.Cells(scale: 5, output: CellOutput.Edge, seed: 4)
            .Warp(Noise.Perlin(scale: 4, octaves: 2), amount: 0.006f)
            .Levels(0f, 0.08f)
            .Invert()
            .Blur(0.004f);
        var cloth = weave.Multiply(0.75f).Add(fuzz.Multiply(0.12f)).Subtract(creases.Multiply(0.25f));

        // A square of older, finer sacking sewn over a hole, cut by hand so its edges wander a little. Its threads
        // don't line up with the sack's: they start part of a thread over and run a few degrees off square, as a patch
        // stitched on by eye would.
        var patch = Shape.Of(ShapeKind.Square, PatchSize)
            .Warp(Noise.Perlin(scale: 6, octaves: 2, seed: 20), amount: 0.0012f)
            .Transform(offset: PatchCentre - new Vector2(0.5f))
            .Levels(0.4f, 0.6f)
            .Blur(0.0004f);
        float turn = 3.5f * 3.14159265f / 180f;
        float cos = DMath.Cos(turn);
        float sin = DMath.Sin(turn);
        var patchWeave = Pixel.Gray(p =>
        {
            var d = p.Uv - PatchCentre;
            var turned = new Vector2(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos);
            return Weave(turned * 112f + new Vector2(0.37f, 0.61f), p.SampleGray(0, turned + new Vector2(0.21f, 0.43f)),
                p.SampleGray(1, turned + new Vector2(0.67f, 0.12f)));
        }, warpSlub, weftSlub);

        // The patch's edge is turned under, so a band round it stands a double layer thick, and a running stitch of
        // pale twine holds it about 4 mm in from the edge.
        var fromEdge = patch.Invert().Distance(0.014f).Multiply(patch);
        var hem = fromEdge.Levels(0.25f, 0.6f);
        var stitches = Pixel.Gray(p =>
        {
            float line = 1f - MathF.Abs(p.Gray(0) - 0.7f) / 0.14f;
            if (line <= 0f || p.Gray(1) < 0.5f)
            {
                return 0f;
            }

            float along = (p.Uv.X + p.Uv.Y) * 60f;
            float dash = along - MathF.Floor(along);
            float stitch = 1f - MathF.Abs(dash - 0.3f) / 0.3f;
            return stitch > 0f ? MathF.Sqrt(line * MathF.Min(1f, stitch * 3f)) : 0f;
        }, fromEdge, patch);
        var patchHeight = patchWeave.Multiply(0.7f).Add(0.28f).Add(hem.Multiply(0.12f)).Add(stitches.Multiply(0.2f));

        // Loose threads pulled out of the weave, lying across the cloth in loose curls a few centimetres long.
        var strand = Pixel.Gray(p =>
        {
            float along = (p.Uv.X - 0.05f) / 0.9f;
            if (along < 0f || along > 1f)
            {
                return 0f;
            }

            float middle = 0.5f + 0.14f * DMath.Sin(along * 5.2f + 0.6f) + 0.04f * DMath.Sin(along * 14f);
            float across = MathF.Abs(p.Uv.Y - middle) / 0.02f;
            return across < 1f ? MathF.Sqrt(1f - across * across) : 0f;
        });
        var loose = strand.Splatter(count: 26, sizeMin: 0.03f, sizeMax: 0.08f, seed: 21);

        var height = cloth
            .Blend(patchHeight, mask: patch)
            .Max(loose.Multiply(0.95f));

        // Pale straw-brown jute, each thread a slightly different shade, darker in the gaps.
        var shade = warpSlub.Add(weftSlub).Multiply(0.5f);
        var sacking = shade
            .GradientMap(Ramp.Of("#7d6240", "#937650", "#a2855c"))
            .Multiply(weave.Levels(0f, 0.6f).Remap(0.35f, 1f));
        var patchCloth = shade
            .GradientMap(Ramp.Of("#6f6049", "#7f6d52", "#8c7a5c"))
            .Multiply(patchWeave.Levels(0f, 0.6f).Remap(0.45f, 1f))
            .Blend(Rgb.Of("#c4b089"), mask: stitches);
        var color = sacking
            .Blend(patchCloth, mask: patch)
            .Multiply(creases.Remap(1f, 0.82f));

        // A dark stain where something wet soaked in, with a tide mark darker still round its rim where it dried.
        var wetSpread = Noise.Perlin(scale: 3, octaves: 5, seed: 30).Warp(Noise.Perlin(scale: 12, octaves: 2, seed: 31), amount: 0.004f);
        var wet = wetSpread.HistogramScan(position: 0.13f, contrast: 0.92f);
        var tide = wet.Subtract(wetSpread.HistogramScan(position: 0.09f, contrast: 0.85f)).Levels(0f, 0.6f);
        color = color
            .Blend(Rgb.Of("#4a3f2e"), mask: wet, opacity: 0.85f)
            .Blend(Rgb.Of("#3a3124"), mask: tide, opacity: 0.6f);

        // Flour dusted on from the grain inside, caught on the tops of the threads and not down in the gaps.
        var flourPatch = Noise.Perlin(scale: 4, octaves: 4, seed: 32).HistogramScan(position: 0.22f, contrast: 0.4f);
        var flour = flourPatch
            .Multiply(height.Levels(0.15f, 0.6f))
            .Multiply(Noise.White(seed: 33).Blur(0.0008f).Levels(0.3f, 0.7f).Remap(0.6f, 1f));
        color = color.Blend(Rgb.Of("#ddd5c2"), mask: flour, opacity: 0.9f);

        // The loose threads are paler than the cloth, being clean on the side that was inside the weave.
        color = color.Blend(Noise.White(seed: 34).Blur(0.001f).GradientMap(Ramp.Of("#9c8159", "#b39668")), mask: loose.Levels(0f, 0.3f));

        // Grubby in broad patches where it has been handled, and grimy from the bottom up, where it stood on the yard
        // and the damp wicked up into it in uneven tongues. The ramp runs down the image, 1 at the foot of a sack.
        var handled = Noise.Perlin(scale: 3, octaves: 5, seed: 5).HistogramScan(position: 0.3f, contrast: 0.25f);
        var foot = Shape.Of(ShapeKind.Gradient)
            .Transform(rotate: -90f)
            .Add(Noise.Perlin(scale: 6, octaves: 3, seed: 35).Remap(-0.12f, 0.12f))
            .Levels(0.62f, 0.98f, gamma: 0.8f);
        color = color
            .Blend(Rgb.Of("#5a4630"), mask: handled, opacity: 0.4f)
            .Blend(Rgb.Of("#3f3426"), mask: foot, opacity: 0.75f);

        return new Material
        {
            Width = 0.5f,
            Depth = 0.004f,
            Height = height,
            Color = color,
            // Dry jute is very dull; patches rubbed smooth, the wet stain and handling leave it a touch less so, the
            // flour more.
            Roughness = weave.Remap(0.95f, 0.89f)
                .Subtract(Noise.Perlin(scale: 4, octaves: 3, seed: 36).Levels(0.35f, 0.7f).Multiply(0.06f))
                .Subtract(wet.Multiply(0.03f))
                .Subtract(handled.Multiply(0.02f))
                .Max(0.85f)
                .Blend(Gray.Of(0.95f), mask: flour),
        };
    }

    /// <summary>
    /// A plain weave at <paramref name="t"/>, in threads: each thread goes over one and under the next. Where a thread
    /// is on top it humps up across the one below; where it is underneath it dips. The threads are round in section and
    /// narrower than the spacing, so light shows through the gaps. The slubs, 0 to 1, make each thread thicker or thinner.
    /// </summary>
    private static float Weave(Vector2 t, float warpSlub, float weftSlub)
    {
        float i = MathF.Floor(t.X);
        float j = MathF.Floor(t.Y);
        float fu = t.X - i;
        float fv = t.Y - j;
        float over = ((int)(i + j) & 1) == 0 ? 1f : -1f;
        float warpWidth = 0.62f + 0.36f * warpSlub;
        float weftWidth = 0.62f + 0.36f * weftSlub;
        float warpT = MathF.Max(0f, 1f - MathF.Abs(fu - 0.5f) * 2f / warpWidth);
        float weftT = MathF.Max(0f, 1f - MathF.Abs(fv - 0.5f) * 2f / weftWidth);
        float warpRound = MathF.Sqrt(warpT * (2f - warpT));
        float weftRound = MathF.Sqrt(weftT * (2f - weftT));
        float warp = warpRound * (0.62f + 0.38f * over * DMath.Sin(3.14159265f * fv));
        float weft = weftRound * (0.62f - 0.38f * over * DMath.Sin(3.14159265f * fu));
        return MathF.Max(warp, weft);
    }
}
