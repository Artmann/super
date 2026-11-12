using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition.Gpu.Tests;

/// <summary>
/// The baker's filter and dilation, <see cref="Baker.FilterAndDilate"/>: pages of made-up light over real texel setups,
/// so what goes in is known exactly, and one real bake for the noise.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class BakeFilterGpuTests(GpuFixture gpu)
{
    private const int TexelsPerMetre = 20;

    private static readonly BakedSpace Room = new("filter-room", new Vector3(-6f, -3f, -6f), new Vector3(6f, 7f, 6f), 1f);

    [Fact]
    public void Filtering_keeps_a_flat_lightmap_flat()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var draws = new[] { StaticDraw(quad, Matrix4x4.CreateScale(2f), 1) };
            using var baker = new Baker(gpu.Context, gpu.Renderer);
            var layout = SetUp(baker, draws);

            // Every valid texel has the same light: irradiance (0.7, 0.3, 0.2), a direction sum of (0, 0.5, 0) over a
            // weight of 0.5, which is the mean direction (0, 1, 0), and a mask of 0.6 on every channel.
            var traced = Synthetic(baker, layout, 0, (_, _) => new Light(new Vector3(0.7f, 0.3f, 0.2f), new Vector4(0f, 0.5f, 0f, 0.5f), 0.6f));

            var filtered = baker.FilterAndDilate(traced, smooth: true);

            // The weights of every tap add up to one, so a flat lightmap comes out the same everywhere, gutters too.
            int filled = 0;
            for (int i = 0; i < filtered.Filled.Length; i++)
            {
                if (!filtered.Filled[i])
                {
                    continue;
                }

                filled++;
                AssertNear(new Vector3(0.7f, 0.3f, 0.2f), Rgb(filtered.Irradiance, i), 1e-4f, $"irradiance of texel {i}");
                AssertNear(new Vector3(0f, 1f, 0f), Rgb(filtered.Direction, i), 1e-4f, $"direction of texel {i}");
                Assert.True(MathF.Abs(filtered.Mask[i * 4] - 0.6f) < 1e-4f, $"The mask of texel {i} is {filtered.Mask[i * 4]}, not 0.6.");
            }

            // The region is the rectangle with 2 texels round it, clamped to the page, and dilation fills all of it.
            Assert.Equal(filtered.Width * filtered.Height, filled);
            Assert.True(filtered.Width >= 42 && filtered.Height >= 42,
                $"The region is {filtered.Width} x {filtered.Height}, not the 40 x 40 rectangle and a gutter.");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Filtering_lowers_noise()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            // A sky bright straight up and black at the horizon and below, over an open floor 2 metres square: every
            // texel sees the same sky, so the true light is flat and all the variation is the path tracer's noise.
            gpu.Renderer.SetEnvironment(new EnvironmentDescription
            {
                Ceiling = new Vector3(1f),
                Horizon = Vector3.Zero,
                Floor = Vector3.Zero,
            });
            var floor = StaticDraw(quad, Floor(), 2);

            // The same seed both times, so the only difference is the filter.
            var raw = Bake([floor], new BakeSettings(TexelsPerMetre, BakeQuality.Preview, filter: false));
            var smooth = Bake([floor], new BakeSettings(TexelsPerMetre, BakeQuality.Preview, filter: true));

            var (rawMean, rawVariance) = Spread(raw, floor);
            var (smoothMean, smoothVariance) = Spread(smooth, floor);
            Assert.True(rawVariance > 0f, "The unfiltered bake has no noise, so this test shows nothing.");
            Assert.True(smoothVariance <= 0.5f * rawVariance,
                $"The filtered variance is {smoothVariance}, not at most half the unfiltered {rawVariance}.");

            // Smoothing moves light about but neither adds nor takes it away.
            Assert.True(MathF.Abs(smoothMean - rawMean) <= 0.02f * rawMean,
                $"The filtered mean is {smoothMean}, not within 2% of the unfiltered {rawMean}.");
        }
        finally
        {
            gpu.Renderer.SetEnvironment(new EnvironmentDescription
            {
                Ceiling = new Vector3(0.5f),
                Horizon = new Vector3(0.4f),
                Floor = new Vector3(0.2f),
            });
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Filtering_smooths_blotches_several_texels_wide()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            // A 4 metre square, 80 x 80 texels, lit in a checkerboard of 8 x 8 texel blocks, 0 and 2. Steps of 1 and 2
            // texels barely reach past a block, so only the passes with steps of 4 and 8 can even it out. Each texel is
            // as noisy as a variance of 10000 says, a standard deviation of 100, so the blocks' steps of 2 are noise the
            // luminance term lets the filter smooth.
            var draws = new[] { StaticDraw(quad, Matrix4x4.CreateScale(4f), 10) };
            using var baker = new Baker(gpu.Context, gpu.Renderer);
            var layout = SetUp(baker, draws);
            Assert.True(layout.TryGet(draws[0].BakeId, out var rect), "The quad has no rectangle.");
            var (x0, y0, side) = Texels(rect);
            Assert.Equal(80, side);
            const int Block = 8;
            var traced = Synthetic(baker, layout, rect.Page, (_, at) =>
            {
                int blocks = (at.X - x0 + side) / Block + (at.Y - y0 + side) / Block;
                return new Light(new Vector3(blocks % 2 == 0 ? 0f : 2f), new Vector4(0f, 0f, 1f, 1f), 1f, Variance: 1e4f);
            });

            var filtered = baker.FilterAndDilate(traced, smooth: true);

            // Over the middle 40 x 40 texels the checkerboard's variance is 1; filtered, the light is close to its
            // mean of 1 everywhere.
            var values = new List<float>();
            for (int y = y0 + 20; y < y0 + 60; y++)
            {
                for (int x = x0 + 20; x < x0 + 60; x++)
                {
                    values.Add(filtered.Irradiance[((y - traced.Y) * traced.Width + x - traced.X) * 4]);
                }
            }

            float mean = values.Average();
            float variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
            Assert.True(MathF.Abs(mean - 1f) <= 0.05f, $"The filtered mean is {mean}, not 1.");
            Assert.True(variance <= 0.05f, $"The filtered variance is {variance}, not at most 0.05 of the blocks' 1.");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Filtering_does_not_blend_across_a_crease()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var tangent = new Vector4(1f, 0f, 0f, 1f);

        // One mesh folded into an L: a floor 2 x 1 metres facing up and a wall 2 x 1 metres facing it, meeting along
        // the x axis. Its second UVs lay both out in one rectangle, the wall's half beside the floor's, so they are
        // neighbours on the page across the fold.
        Vertex[] vertices =
        [
            new(new Vector3(-1f, 0f, 0f), Vector3.UnitY, tangent, Vector2.Zero) { Uv2 = new Vector2(0f, 0.5f) },
            new(new Vector3(1f, 0f, 0f), Vector3.UnitY, tangent, Vector2.Zero) { Uv2 = new Vector2(1f, 0.5f) },
            new(new Vector3(1f, 0f, 1f), Vector3.UnitY, tangent, Vector2.Zero) { Uv2 = new Vector2(1f, 1f) },
            new(new Vector3(-1f, 0f, 1f), Vector3.UnitY, tangent, Vector2.Zero) { Uv2 = new Vector2(0f, 1f) },
            new(new Vector3(-1f, 0f, 0f), Vector3.UnitZ, tangent, Vector2.Zero) { Uv2 = new Vector2(0f, 0.5f) },
            new(new Vector3(1f, 0f, 0f), Vector3.UnitZ, tangent, Vector2.Zero) { Uv2 = new Vector2(1f, 0.5f) },
            new(new Vector3(1f, 1f, 0f), Vector3.UnitZ, tangent, Vector2.Zero) { Uv2 = new Vector2(1f, 0f) },
            new(new Vector3(-1f, 1f, 0f), Vector3.UnitZ, tangent, Vector2.Zero) { Uv2 = new Vector2(0f, 0f) },
        ];
        var fold = gpu.Renderer.CreateMesh(vertices, [0, 1, 2, 0, 2, 3, 4, 5, 6, 4, 6, 7]);
        gpu.RenderOneFrame(); // Uploads the mesh.
        try
        {
            var draw = StaticDraw(fold, Matrix4x4.Identity, 11) with { IndexCount = 12 };
            using var baker = new Baker(gpu.Context, gpu.Renderer);
            var layout = SetUp(baker, [draw]);
            Assert.True(layout.TryGet(draw.BakeId, out var rect), "The fold has no rectangle.");
            var traced = Synthetic(baker, layout, rect.Page, (_, _) => Light.Dark);

            // The wall holds 1 and the floor 0, told apart by each texel's geometric normal.
            float[] texels = baker.ReadTexels(rect.Page, traced.X, traced.Y, traced.Width, traced.Height);
            bool OnWall(int i) => texels[i * Baker.TexelFloats + 6] > texels[i * Baker.TexelFloats + 5];
            for (int i = 0; i < traced.Covered.Length; i++)
            {
                if (traced.Covered[i] && OnWall(i))
                {
                    for (int c = 0; c < 3; c++)
                    {
                        traced.Irradiance[i * 4 + c] = 1f;
                    }
                }
            }

            var filtered = baker.FilterAndDilate(traced, smooth: true);

            // Every valid texel keeps its own face's light: the floor's texels beside the fold are on the same chart
            // and close in the world, but off the wall's plane and turned away from it.
            int wall = 0;
            int floor = 0;
            for (int i = 0; i < traced.Covered.Length; i++)
            {
                if (!traced.Valid[i])
                {
                    continue;
                }

                float expected = OnWall(i) ? 1f : 0f;
                if (OnWall(i))
                {
                    wall++;
                }
                else
                {
                    floor++;
                }

                float light = filtered.Irradiance[i * 4];
                Assert.True(MathF.Abs(light - expected) <= 1e-3f,
                    $"Texel ({i % traced.Width}, {i / traced.Width}) on the {(OnWall(i) ? "wall" : "floor")} holds {light}, not {expected}.");
            }

            // Each face is 2 x 1 metres; its rectangle's side follows the fold's area, so check both faces were seen.
            Assert.True(wall >= 100 && floor >= 100, $"Only {wall} wall and {floor} floor texels are valid.");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(fold);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Filtering_does_not_blend_across_charts(bool forceMultisample)
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            // Two square metres side by side in the same plane, touching along x = 0, so the texels at the seam are
            // neighbours in the world as well as close on the page.
            var lit = StaticDraw(quad, Matrix4x4.CreateTranslation(-0.5f, 0f, 0f), 3);
            var dark = StaticDraw(quad, Matrix4x4.CreateTranslation(0.5f, 0f, 0f), 4);
            using var baker = new Baker(gpu.Context, gpu.Renderer) { ForceMultisample = forceMultisample };
            var layout = SetUp(baker, [lit, dark]);
            Assert.True(layout.TryGet(lit.BakeId, out var litRect), "The lit quad has no rectangle.");
            Assert.True(layout.TryGet(dark.BakeId, out var darkRect), "The dark quad has no rectangle.");
            Assert.Equal(litRect.Page, darkRect.Page);
            float darkChart = ChartOf(baker, darkRect);

            // Every texel drawn for the lit quad holds 1 everywhere; the dark quad's hold 0.
            var traced = Synthetic(baker, layout, litRect.Page,
                (chart, _) => chart == darkChart ? Light.Dark : new Light(Vector3.One, new Vector4(0f, 1f, 0f, 1f), 1f));

            var filtered = baker.FilterAndDilate(traced, smooth: true);

            // No texel drawn for the dark quad takes any of the lit one's light, even at the seam.
            float[] texels = baker.ReadTexels(litRect.Page, traced.X, traced.Y, traced.Width, traced.Height);
            int darkTexels = 0;
            for (int i = 0; i < traced.Covered.Length; i++)
            {
                if (texels[i * Baker.TexelFloats + 3] != darkChart)
                {
                    continue;
                }

                darkTexels++;
                var light = Rgb(filtered.Irradiance, i);
                Assert.True(light == Vector3.Zero,
                    $"Texel ({i % traced.Width}, {i / traced.Width}) of the dark quad holds {light}, not black.");
            }

            // A square metre at 20 texels a metre is 20 x 20 texels, all of them the dark quad's.
            Assert.True(darkTexels >= 20 * 20, $"Only {darkTexels} texels belong to the dark quad, not 20 x 20.");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Dilation_does_not_fill_across_charts()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var (boxVertices, boxIndices) = TestMeshes.Cube();
        var box = gpu.Renderer.CreateMesh(boxVertices, boxIndices);
        gpu.RenderOneFrame(); // Uploads the box.
        try
        {
            // The same two quads, and a thin closed box across the seam from x -0.3 to 0.3, y -0.2 to 0.2 and z -0.01
            // to 0.01, so the texels of both quads near the seam lie inside a wall and are invalid. The box isn't
            // static, so it gets no rectangle, but rays still hit it.
            var lit = StaticDraw(quad, Matrix4x4.CreateTranslation(-0.5f, 0f, 0f), 8);
            var dark = StaticDraw(quad, Matrix4x4.CreateTranslation(0.5f, 0f, 0f), 9);
            var wall = new DrawItem
            {
                Mesh = box, IndexCount = 36, Material = gpu.Matte,
                Transform = Matrix4x4.CreateScale(0.6f, 0.4f, 0.02f),
            };
            using var baker = new Baker(gpu.Context, gpu.Renderer);
            Assert.True(baker.Conservative,
                "This test needs conservative rasterization, which spills each quad into the gutter so the two touch.");
            var layout = SetUp(baker, [lit, dark, wall]);
            Assert.True(layout.TryGet(lit.BakeId, out var litRect), "The lit quad has no rectangle.");
            Assert.True(layout.TryGet(dark.BakeId, out var darkRect), "The dark quad has no rectangle.");
            float darkChart = ChartOf(baker, darkRect);
            var traced = Synthetic(baker, layout, litRect.Page,
                (chart, _) => chart == darkChart ? Light.Dark : new Light(Vector3.One, new Vector4(0f, 1f, 0f, 1f), 1f));

            // Unsmoothed, so only dilation can move light.
            var filtered = baker.FilterAndDilate(traced, smooth: false);

            // The dark quad's invalid texels are filled from its own valid ones, all black, never from the lit quad's
            // texels beside them on the page.
            float[] texels = baker.ReadTexels(litRect.Page, traced.X, traced.Y, traced.Width, traced.Height);
            int invalid = 0;
            for (int i = 0; i < traced.Covered.Length; i++)
            {
                if (texels[i * Baker.TexelFloats + 3] != darkChart)
                {
                    continue;
                }

                if (!traced.Valid[i])
                {
                    invalid++;
                    Assert.True(filtered.Filled[i], $"Invalid texel ({i % traced.Width}, {i / traced.Width}) of the dark quad isn't filled.");
                }

                var light = Rgb(filtered.Irradiance, i);
                Assert.True(light == Vector3.Zero,
                    $"Texel ({i % traced.Width}, {i / traced.Width}) of the dark quad holds {light}, not black.");
            }

            Assert.True(invalid > 0, "None of the dark quad's texels lie inside the box, so this test shows nothing.");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
            gpu.Renderer.DestroyMesh(box);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Dilation_fills_the_gutter(bool smooth)
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var draws = new[] { StaticDraw(quad, Matrix4x4.Identity, 5) };
            using var baker = new Baker(gpu.Context, gpu.Renderer);
            var layout = SetUp(baker, draws);
            Assert.True(layout.TryGet(draws[0].BakeId, out var rect), "The quad has no rectangle.");
            var traced = Synthetic(baker, layout, rect.Page, (_, _) => new Light(new Vector3(0.25f, 0.5f, 1f), new Vector4(1f, 0f, 0f, 1f), 0.75f));

            var filtered = baker.FilterAndDilate(traced, smooth);

            // Every texel of the rectangle and of the gutter round it on the page, LightmapUnwrap.Gutter = 2 texels deep,
            // holds the quad's light, so bilinear filtering at the edge reads the quad and not black. The rectangle may
            // sit in the page's corner, but there is always a gutter to its right and below.
            var (x0, y0, side) = Texels(rect);
            int gutter = LightmapUnwrap.Gutter;
            Assert.True(traced.X + traced.Width >= x0 + side + gutter && traced.Y + traced.Height >= y0 + side + gutter,
                "The traced region doesn't reach past the gutter.");
            for (int y = Math.Max(y0 - gutter, 0); y < y0 + side + gutter; y++)
            {
                for (int x = Math.Max(x0 - gutter, 0); x < x0 + side + gutter; x++)
                {
                    int i = (y - traced.Y) * traced.Width + (x - traced.X);
                    Assert.True(filtered.Filled[i], $"Texel ({x - x0}, {y - y0}) of the rectangle isn't filled.");
                    AssertNear(new Vector3(0.25f, 0.5f, 1f), Rgb(filtered.Irradiance, i), 1e-4f, $"irradiance at ({x - x0}, {y - y0})");
                    AssertNear(Vector3.UnitX, Rgb(filtered.Direction, i), 1e-4f, $"direction at ({x - x0}, {y - y0})");
                    Assert.True(MathF.Abs(filtered.Mask[i * 4] - 0.75f) < 1e-4f,
                        $"The mask at ({x - x0}, {y - y0}) is {filtered.Mask[i * 4]}, not 0.75.");
                }
            }

            // The encoded page holds it too, out to the gutter's far corner.
            var encoded = Baker.Encode(filtered);
            var halves = MemoryMarshal.Cast<byte, Half>(encoded.Irradiance.Pixels.AsSpan());
            int corner = ((y0 + side + gutter - 1) * LightmapLayout.PageSize + x0 + side + gutter - 1) * 4;
            Assert.Equal(0.25f, (float)halves[corner]);
            Assert.Equal(1f, (float)halves[corner + 3]);
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Values_that_are_not_finite_are_cleared(bool smooth)
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var draws = new[] { StaticDraw(quad, Matrix4x4.Identity, 6) };
            using var baker = new Baker(gpu.Context, gpu.Renderer);
            var layout = SetUp(baker, draws);
            Assert.True(layout.TryGet(draws[0].BakeId, out var rect), "The quad has no rectangle.");
            var (x0, y0, _) = Texels(rect);

            // Light of 1 everywhere but three texels of the rectangle: one not a number, one infinite and one too
            // bright for a half float, as a bad path could leave them.
            int Index(TracedPage page, int x, int y) => (y0 + y - page.Y) * page.Width + (x0 + x - page.X);
            var traced = Synthetic(baker, layout, rect.Page, (_, _) => new Light(Vector3.One, new Vector4(0f, 0f, 1f, 1f), 1f));
            int nan = Index(traced, 5, 5);
            int infinite = Index(traced, 10, 10);
            int huge = Index(traced, 15, 15);
            for (int c = 0; c < 4; c++)
            {
                traced.Irradiance[nan * 4 + c] = float.NaN;
                traced.Direction[nan * 4 + c] = float.NaN;
                traced.Mask[nan * 4 + c] = float.NaN;
                traced.Irradiance[infinite * 4 + c] = float.PositiveInfinity;
                traced.Direction[infinite * 4 + c] = float.NegativeInfinity;
                traced.Mask[infinite * 4 + c] = float.PositiveInfinity;
                traced.Irradiance[huge * 4 + c] = 1e6f;
            }

            var filtered = baker.FilterAndDilate(traced, smooth);

            // Not a number becomes 0, and too big becomes the half float's 65504, so every texel is a number the file
            // can hold and nothing past it.
            for (int i = 0; i < filtered.Filled.Length; i++)
            {
                for (int c = 0; c < 4; c++)
                {
                    float[][] all = [filtered.Irradiance, filtered.Direction, filtered.Mask];
                    foreach (var values in all)
                    {
                        float value = values[i * 4 + c];
                        Assert.True(float.IsFinite(value) && MathF.Abs(value) <= 65504f,
                            $"Texel ({i % filtered.Width}, {i / filtered.Width}) holds {value}.");
                    }
                }
            }

            if (!smooth)
            {
                // Unsmoothed, each texel keeps its own cleared value.
                Assert.Equal(0f, filtered.Irradiance[nan * 4]);
                Assert.Equal(65504f, filtered.Irradiance[infinite * 4]);
                Assert.Equal(65504f, filtered.Irradiance[huge * 4]);
                Assert.Equal(1f, filtered.Mask[infinite * 4]);
            }
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Filtering_keeps_the_mask_sharp()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var draws = new[] { StaticDraw(quad, Matrix4x4.Identity, 7) };
            using var baker = new Baker(gpu.Context, gpu.Renderer);
            var layout = SetUp(baker, draws);
            Assert.True(layout.TryGet(draws[0].BakeId, out var rect), "The quad has no rectangle.");
            var (x0, _, side) = Texels(rect);

            // A shadow edge down the middle of the rectangle: masked to the left of column 10, lit from it on.
            var traced = Synthetic(baker, layout, rect.Page,
                (_, at) => new Light(Vector3.One, new Vector4(0f, 0f, 1f, 1f), at.X < x0 + 10 ? 0f : 1f));

            var filtered = baker.FilterAndDilate(traced, smooth: true);

            var (_, y0, _) = Texels(rect);
            for (int y = y0; y < y0 + side; y++)
            {
                for (int x = x0; x < x0 + side; x++)
                {
                    int i = (y - traced.Y) * traced.Width + (x - traced.X);
                    float expected = x < x0 + 10 ? 0f : 1f;
                    Assert.True(filtered.Mask[i * 4] == expected,
                        $"The mask at ({x - x0}, {y - y0}) is {filtered.Mask[i * 4]}, not {expected}.");
                }
            }
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    /// <summary>
    /// The light made up for one texel: irradiance, the direction sum and its weight, every mask channel, and the
    /// variance of the irradiance's mean luminance, which tells the filter how noisy the texel is.
    /// </summary>
    private readonly record struct Light(Vector3 Irradiance, Vector4 Direction, float Mask, float Variance = 0f)
    {
        public static Light Dark => new(Vector3.Zero, Vector4.Zero, 0f);
    }

    /// <summary>
    /// A traced page over the region <see cref="Baker.Trace"/> would cover, with <paramref name="light"/> of each texel's
    /// rectangle number and page position on its covered texels, as the path tracer would leave it.
    /// </summary>
    private static TracedPage Synthetic(Baker baker, LightmapLayout layout, int page, Func<float, (int X, int Y), Light> light)
    {
        var (x, y, width, height) = Baker.UsedRegion(layout, page);
        float[] texels = baker.ReadTexels(page, x, y, width, height);
        int count = width * height;
        var irradiance = new float[count * 4];
        var direction = new float[count * 4];
        var mask = new float[count * 4];
        var covered = new bool[count];
        var valid = new bool[count];
        for (int i = 0; i < count; i++)
        {
            float chart = texels[i * Baker.TexelFloats + 3];
            covered[i] = chart > 0.5f;
            valid[i] = texels[i * Baker.TexelFloats + 10] > 0.5f;
            if (!covered[i])
            {
                continue;
            }

            var made = light(chart, (x + i % width, y + i / width));
            irradiance[i * 4 + 0] = made.Irradiance.X;
            irradiance[i * 4 + 1] = made.Irradiance.Y;
            irradiance[i * 4 + 2] = made.Irradiance.Z;
            irradiance[i * 4 + 3] = made.Variance;
            direction[i * 4 + 0] = made.Direction.X;
            direction[i * 4 + 1] = made.Direction.Y;
            direction[i * 4 + 2] = made.Direction.Z;
            direction[i * 4 + 3] = made.Direction.W;
            for (int c = 0; c < 4; c++)
            {
                mask[i * 4 + c] = made.Mask;
            }
        }

        return new TracedPage(page, x, y, width, height, irradiance, direction, mask, covered, valid, valid);
    }

    /// <summary>The rectangle number the texel setup gave the middle texel of <paramref name="rect"/>.</summary>
    private static float ChartOf(Baker baker, LightmapRect rect)
    {
        var (x0, y0, side) = Texels(rect);
        float chart = baker.ReadTexels(rect.Page, x0 + side / 2, y0 + side / 2, 1, 1)[3];
        Assert.True(chart > 0.5f, "The rectangle's middle texel isn't covered.");
        return chart;
    }

    private LightmapLayout SetUp(Baker baker, DrawItem[] draws)
    {
        var layout = LightmapLayout.Build(draws, gpu.Renderer.AreaOf, new BakeSettings(TexelsPerMetre), _ => true);
        gpu.Context.SubmitAndWait(cmd => baker.SetUpTexels(cmd, layout, draws));
        return layout;
    }

    // The test quad turned to face up, 2 metres square.
    private static Matrix4x4 Floor() =>
        Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationX(-MathF.PI / 2f);

    private LightBakeFile Bake(DrawItem[] draws, BakeSettings settings)
    {
        var view = gpu.View(DebugView.None);
        view.BakedSpace = Room with { TexelsPerMetre = settings.TexelsPerMetre };
        view.EnvironmentIntensity = 1f;
        return LightBakeFile.Read(gpu.Renderer.Bake(view, draws, settings));
    }

    /// <summary>The mean and variance of the red irradiance over the middle 30 x 30 of the floor's 40 x 40 texels.</summary>
    private static (float Mean, float Variance) Spread(LightBakeFile file, DrawItem floor)
    {
        LightmapRect rect = default;
        bool found = false;
        foreach (var (bakeId, drawRect) in file.Draws)
        {
            if (bakeId == floor.BakeId)
            {
                rect = drawRect;
                found = true;
            }
        }

        Assert.True(found, "The bake has no rectangle for the floor.");
        var (x0, y0, side) = Texels(rect);
        Assert.Equal(40, side);
        var halves = MemoryMarshal.Cast<byte, Half>(file.Irradiance![rect.Page].Pixels.AsSpan());
        var values = new List<float>();
        for (int y = 5; y < 35; y++)
        {
            for (int x = 5; x < 35; x++)
            {
                values.Add((float)halves[((y0 + y) * LightmapLayout.PageSize + x0 + x) * 4]);
            }
        }

        float mean = values.Average();
        float variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
        return (mean, variance);
    }

    private MeshHandle CreateQuad()
    {
        var (vertices, indices) = TestMeshes.Quad();
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].Uv2 = vertices[i].Uv;
        }

        var handle = gpu.Renderer.CreateMesh(vertices, indices);
        gpu.RenderOneFrame(); // Uploads the mesh.
        return handle;
    }

    private DrawItem StaticDraw(MeshHandle mesh, Matrix4x4 transform, ulong instance) => new()
    {
        Mesh = mesh,
        IndexCount = 6,
        Material = gpu.Matte,
        Transform = transform,
        // An id keeps an instance hash's top 52 bits, so small numbers are moved up past the part's 12.
        BakeId = BakeIds.Of(instance << 12, 0),
    };

    private static (int X, int Y, int Side) Texels(LightmapRect rect) => (
        (int)MathF.Round(rect.Offset.X * LightmapLayout.PageSize),
        (int)MathF.Round(rect.Offset.Y * LightmapLayout.PageSize),
        (int)MathF.Round(rect.Scale.X * LightmapLayout.PageSize));

    private static Vector3 Rgb(float[] values, int texel) => new(values[texel * 4], values[texel * 4 + 1], values[texel * 4 + 2]);

    private static void AssertNear(Vector3 expected, Vector3 actual, float tolerance, string what)
    {
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"The {what} is {actual}, not {expected}.");
    }
}
