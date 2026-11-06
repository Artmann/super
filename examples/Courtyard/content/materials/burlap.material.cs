using Super.Materials;

/// <summary>Coarse sacking: thick, uneven jute threads in a loose plain weave, with dark gaps between them and grubby patches.</summary>
public sealed class Burlap : MaterialGraph
{
    public override Material Build()
    {
        // 96 threads each way across the 0.5 m, about 5 mm apart. Jute is lumpy, so each thread
        // swells and thins along its length: noise stretched along it, one cell per thread across.
        const float threads = 96f;
        var warpSlub = Noise.Directional(scale: 96, angle: 90f, stretch: 12f, octaves: 2, seed: 1);
        var weftSlub = Noise.Directional(scale: 96, angle: 0f, stretch: 12f, octaves: 2, seed: 2);

        // A plain weave: each thread goes over one and under the next. Where a thread is on top
        // it humps up across the one below; where it is underneath it dips. The threads are
        // round in section and narrower than the spacing, so light shows through the gaps.
        var weave = Pixel.Gray(p =>
        {
            float u = p.Uv.X * threads;
            float v = p.Uv.Y * threads;
            float i = MathF.Floor(u);
            float j = MathF.Floor(v);
            float fu = u - i;
            float fv = v - j;
            float over = ((int)(i + j) & 1) == 0 ? 1f : -1f;
            float warpWidth = 0.62f + 0.36f * p.Gray(0);
            float weftWidth = 0.62f + 0.36f * p.Gray(1);
            float warpT = MathF.Max(0f, 1f - MathF.Abs(fu - 0.5f) * 2f / warpWidth);
            float weftT = MathF.Max(0f, 1f - MathF.Abs(fv - 0.5f) * 2f / weftWidth);
            float warpRound = MathF.Sqrt(warpT * (2f - warpT));
            float weftRound = MathF.Sqrt(weftT * (2f - weftT));
            float warp = warpRound * (0.62f + 0.38f * over * DMath.Sin(3.14159265f * fv));
            float weft = weftRound * (0.62f - 0.38f * over * DMath.Sin(3.14159265f * fu));
            return MathF.Max(warp, weft);
        }, warpSlub, weftSlub);

        // Loose fibres standing off the threads, and the cloth's own sag and creases.
        var fuzz = Noise.White(seed: 3).Blur(0.0006f).Levels(0.45f, 0.75f);
        var creases = Noise.Cells(scale: 5, output: CellOutput.Edge, seed: 4)
            .Warp(Noise.Perlin(scale: 4, octaves: 2), amount: 0.006f)
            .Levels(0f, 0.08f)
            .Invert()
            .Blur(0.004f);
        var height = weave.Multiply(0.75f).Add(fuzz.Multiply(0.12f)).Subtract(creases.Multiply(0.25f));

        // Pale straw-brown jute, each thread a slightly different shade, darker in the gaps and
        // grubby in broad patches where it has sat on the ground and been handled.
        var shade = warpSlub.Add(weftSlub).Multiply(0.5f);
        var dirt = Noise.Perlin(scale: 3, octaves: 5, seed: 5).HistogramScan(position: 0.3f, contrast: 0.25f);
        var color = shade
            .GradientMap(Ramp.Of("#7d6240", "#937650", "#a2855c"))
            .Multiply(weave.Levels(0f, 0.6f).Remap(0.35f, 1f))
            .Multiply(creases.Remap(1f, 0.7f))
            .Blend(Rgb.Of("#5a4630"), mask: dirt, opacity: 0.55f);

        return new Material
        {
            Width = 0.5f,
            Depth = 0.004f,
            Height = height,
            Color = color,
            Roughness = weave.Remap(0.98f, 0.86f).Add(dirt.Remap(0f, -0.04f)),
        };
    }
}
