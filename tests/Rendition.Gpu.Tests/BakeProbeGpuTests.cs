using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition.Gpu.Tests;

/// <summary>
/// The space's light probes baked by <see cref="Renderer.Bake"/>: traced, folded into the live probes' tiles and read
/// back from the <c>.lightbake</c> file it returns.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class BakeProbeGpuTests(GpuFixture gpu)
{
    // The atlases hold 64 tiles across: 8 texels and a border of 1 each side for irradiance, 16 and 1 for depth.
    private const int IrradianceSide = 10;
    private const int DepthSide = 18;
    private const int IrradianceWidth = 64 * IrradianceSide;
    private const int DepthWidth = 64 * DepthSide;

    private static EnvironmentDescription DefaultEnvironment() => new()
    {
        Ceiling = new Vector3(0.5f),
        Horizon = new Vector3(0.4f),
        Floor = new Vector3(0.2f),
    };

    // A sky of 0.5 in every direction.
    private static EnvironmentDescription EvenSky() => new()
    {
        Ceiling = new Vector3(0.5f),
        Horizon = new Vector3(0.5f),
        Floor = new Vector3(0.5f),
    };

    // Probes a metre apart from (-6, -3, -6) to (6, 7, 6): 13 x 11 x 13 = 1859 of them, x fastest.
    private static readonly BakedSpace Room = new("probe-room", new Vector3(-6f, -3f, -6f), new Vector3(6f, 7f, 6f), 1f);

    // The probe at whole metres (x, y, z): i = x + 6, j = y + 3, k = z + 6, at i + 13 (j + 11 k).
    private static int ProbeAt(int x, int y, int z) => x + 6 + 13 * (y + 3 + 11 * (z + 6));

    // The test quad turned to face up, 2 metres square.
    private static Matrix4x4 Floor(float size = 2f, float height = 0f) =>
        Matrix4x4.CreateScale(size) * Matrix4x4.CreateRotationX(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, height, 0f);

    [Fact]
    public void A_probe_under_a_uniform_sky_reads_its_radiance()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            gpu.Renderer.SetEnvironment(EvenSky());
            var draws = new[] { StaticDraw(quad, Floor(), gpu.Matte, 1) };

            var file = Bake(draws, [], sky: 1f, new BakeSettings(20, BakeQuality.Preview));

            // The probe a metre above the plane. Its two texels nearest straight up look 8 degrees from it, so the half
            // of the sphere round them reaches 8 degrees below the horizon, where the plane, 1 metre wide each side,
            // starts 45 degrees down. They see only sky, and the cosine-weighted mean of L = 0.5 is 0.5.
            int probe = ProbeAt(0, 1, 0);
            foreach (int x in new[] { 3, 4 })
            {
                var up = IrradianceTexel(file, probe, x + 1, 8);
                AssertWithin(0.5f, up.X, 0.02f, $"light reaching texel ({x}, 7) of the probe above the plane");
            }
        }
        finally
        {
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_probe_inside_a_closed_box_reads_black()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            gpu.Renderer.SetEnvironment(EvenSky());

            // Six faces 3.3 metres square, each turned to face into the box and set 1.6 metres out from the probe at
            // the origin, overlapping at the edges so no crack is left.
            const float side = 3.3f;
            const float half = 1.6f;
            Matrix4x4[] faces =
            [
                Matrix4x4.CreateRotationX(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, -half, 0f),
                Matrix4x4.CreateRotationX(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, half, 0f),
                Matrix4x4.CreateRotationY(MathF.PI / 2f) * Matrix4x4.CreateTranslation(-half, 0f, 0f),
                Matrix4x4.CreateRotationY(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(half, 0f, 0f),
                Matrix4x4.CreateTranslation(0f, 0f, -half),
                Matrix4x4.CreateRotationY(MathF.PI) * Matrix4x4.CreateTranslation(0f, 0f, half),
            ];
            var draws = new DrawItem[faces.Length];
            for (int i = 0; i < faces.Length; i++)
            {
                draws[i] = StaticDraw(quad, Matrix4x4.CreateScale(side) * faces[i], gpu.Matte, 10 + (ulong)i);
            }

            var file = Bake(draws, [], sky: 1f, new BakeSettings(8, BakeQuality.Preview));

            // No ray from the probe inside reaches the sky and nothing in the box gives light.
            int inside = ProbeAt(0, 0, 0);
            for (int y = 0; y < IrradianceSide; y++)
            {
                for (int x = 0; x < IrradianceSide; x++)
                {
                    var seen = IrradianceTexel(file, inside, x, y);
                    Assert.True(seen.X < 1e-3f && seen.Y < 1e-3f && seen.Z < 1e-3f,
                        $"Texel ({x}, {y}) of the probe in the box reads {seen}, not black.");
                }
            }

            // The probe 3 metres up, above the box, sees the sky of 0.5 straight up, so the sky was on.
            var above = IrradianceTexel(file, ProbeAt(0, 3, 0), 4, 8);
            AssertWithin(0.5f, above.X, 0.02f, "light reaching the probe above the box");
        }
        finally
        {
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Probe_tiles_match_the_live_layout()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            gpu.Renderer.SetEnvironment(EvenSky());
            var draws = new[] { StaticDraw(quad, Floor(), gpu.Matte, 70) };

            var file = Bake(draws, [], sky: 1f, new BakeSettings(20, BakeQuality.Preview));

            // The probes are the space's: its box and spacing.
            Assert.Equal(new ProbeVolumeSettings(Room.Min, Room.Max, Room.ProbeSpacing), file.Probes);

            // Both atlases are 64 tiles square, as the live ones are.
            Assert.NotNull(file.ProbeIrradiance);
            Assert.NotNull(file.ProbeDepth);
            Assert.Equal((IrradianceWidth, IrradianceWidth, LightBakePageFormat.Rgba16f),
                (file.ProbeIrradiance.Width, file.ProbeIrradiance.Height, file.ProbeIrradiance.Format));
            Assert.Equal((DepthWidth, DepthWidth, LightBakePageFormat.Rgba16f),
                (file.ProbeDepth.Width, file.ProbeDepth.Height, file.ProbeDepth.Format));

            // Probe 916, a metre above the plane, has its tile at column 916 % 64 = 20 of row 916 / 64 = 14, past the
            // first row. Straight up it sees the sky of 0.5; straight down the plane 1 metre away.
            int probe = ProbeAt(0, 1, 0);
            Assert.Equal(916, probe);
            Assert.Equal(new Vector3(0f, 1f, 0f), ProbeGrid.Position(file.Probes!.Value, probe));
            var up = IrradianceTexel(file, probe, 5, 8);
            AssertWithin(0.5f, up.X, 0.02f, "light reaching probe 916 from above");
            Assert.Equal(1f, IrradianceAlpha(file, probe, 5, 8));

            // In a 16 texel depth tile straight down is between interior texels (7, 0) and (8, 0), which look 4 degrees
            // from it at the plane 1 / cos 4 = 1.002 metres away; the cos^50 lobe round them stays on the plane.
            var down = DepthTexel(file, probe, 9, 1);
            AssertWithin(1f, down.X, 0.03f, "mean distance below probe 916");
            AssertWithin(1f, down.Y, 0.06f, "mean square distance below probe 916");

            // The border copies the texel across the octahedral map's edge, mirrored: the left edge's row 3 shows
            // interior (0, 8 - 3) = bordered (1, 6), the top edge's column 3 interior (8 - 3, 0) = bordered (6, 1),
            // and the corner (0, 0) the opposite interior corner (7, 7) = bordered (8, 8).
            Assert.Equal(IrradianceTexel(file, probe, 1, 6), IrradianceTexel(file, probe, 0, 3));
            Assert.Equal(IrradianceTexel(file, probe, 6, 1), IrradianceTexel(file, probe, 3, 0));
            Assert.Equal(IrradianceTexel(file, probe, 8, 8), IrradianceTexel(file, probe, 0, 0));
            Assert.Equal(DepthTexel(file, probe, 1, 14), DepthTexel(file, probe, 0, 3));

            // Every probe of the 1859 sees the sky somewhere, so each tile has light; past them the tiles are black.
            foreach (int lit in new[] { 0, 63, 64, 1858 })
            {
                Assert.True(TileLight(file, lit) > 0f, $"Tile {lit} holds no light.");
            }

            foreach (int empty in new[] { 1859, 4095 })
            {
                Assert.Equal(0f, TileLight(file, empty));
            }
        }
        finally
        {
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_baked_light_reaches_a_probe_and_a_mixed_one_does_not()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var black = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0f, 0f, 0f, 1f), Roughness = 0.8f });
        try
        {
            // A black floor well below, so nothing bounces, and no sky: the probe's only light is the lamp's own.
            var draws = new[] { StaticDraw(quad, Floor(height: -2.5f), black, 80) };
            var settings = new BakeSettings(20, BakeQuality.Preview);

            var baked = Bake(draws, [Lamp(LightMode.Baked, 1109)], sky: 0f, settings);
            var mixed = Bake(draws, [Lamp(LightMode.Mixed, 1110)], sky: 0f, settings);

            // The lamp is a metre straight above the probe, with radiance 10 and radius 0.1, so it gives
            // 10 (1 + 0.01) / (1 + 0.01) = 10 straight up. Texel (4, 7) looks along (0.125, 0.875, 0), at cos 0.98995
            // to it, and holds the cosine-weighted mean radiance: 10 * 0.98995 / pi = 3.151.
            int probe = ProbeAt(0, 1, 0);
            var lit = IrradianceTexel(baked, probe, 5, 8);
            AssertWithin(10f * 0.98995f / MathF.PI, lit.X, 0.03f, "light of the Baked lamp at the probe");

            // A Mixed lamp's direct light is drawn live, and with nothing to bounce off it gives the probe nothing.
            var unlit = IrradianceTexel(mixed, probe, 5, 8);
            Assert.True(unlit.X < 1e-3f, $"The Mixed lamp gives the probe {unlit.X}, not 0.");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
            gpu.Renderer.DestroyMaterial(black);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private static LocalLight Lamp(LightMode mode, int key) => new()
    {
        Mode = mode,
        Key = key,
        Position = new Vector3(0f, 2f, 0f),
        Radiance = new Vector3(10f),
        Range = 100f,
        Radius = 0.1f,
    };

    private LightBakeFile Bake(DrawItem[] draws, LocalLight[] lights, float sky, BakeSettings settings)
    {
        var view = gpu.View(DebugView.None);
        view.BakedSpace = Room;
        view.EnvironmentIntensity = sky;
        view.LocalLights = lights;
        return LightBakeFile.Read(gpu.Renderer.Bake(view, draws, settings));
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

    private static DrawItem StaticDraw(MeshHandle mesh, Matrix4x4 transform, MaterialHandle material, ulong instance) => new()
    {
        Mesh = mesh,
        IndexCount = 6,
        Material = material,
        Transform = transform,
        // An id keeps an instance hash's top 52 bits, so small numbers are moved up past the part's 12.
        BakeId = BakeIds.Of(instance << 12, 0),
    };

    // A texel of a probe's irradiance tile, counted with the border: (1, 1) is the first inside it.
    private static Vector3 IrradianceTexel(LightBakeFile file, int probe, int x, int y)
    {
        var halves = Halves(file.ProbeIrradiance!);
        int at = IrradianceAt(probe, x, y);
        return new Vector3((float)halves[at], (float)halves[at + 1], (float)halves[at + 2]);
    }

    private static float IrradianceAlpha(LightBakeFile file, int probe, int x, int y) =>
        (float)Halves(file.ProbeIrradiance!)[IrradianceAt(probe, x, y) + 3];

    private static int IrradianceAt(int probe, int x, int y) =>
        (((probe / 64) * IrradianceSide + y) * IrradianceWidth + (probe % 64) * IrradianceSide + x) * 4;

    // The mean and mean square distance in a texel of a probe's depth tile, counted with the border.
    private static Vector2 DepthTexel(LightBakeFile file, int probe, int x, int y)
    {
        var halves = Halves(file.ProbeDepth!);
        int at = (((probe / 64) * DepthSide + y) * DepthWidth + (probe % 64) * DepthSide + x) * 4;
        return new Vector2((float)halves[at], (float)halves[at + 1]);
    }

    // The sum of every channel of a probe's irradiance tile.
    private static float TileLight(LightBakeFile file, int probe)
    {
        var halves = Halves(file.ProbeIrradiance!);
        float sum = 0f;
        for (int y = 0; y < IrradianceSide; y++)
        {
            for (int x = 0; x < IrradianceSide; x++)
            {
                int at = IrradianceAt(probe, x, y);
                sum += (float)halves[at] + (float)halves[at + 1] + (float)halves[at + 2];
            }
        }

        return sum;
    }

    private static ReadOnlySpan<Half> Halves(LightBakePage page) => MemoryMarshal.Cast<byte, Half>(page.Pixels.AsSpan());

    private static void AssertWithin(float expected, float actual, float share, string what)
    {
        Assert.True(MathF.Abs(actual - expected) <= share * MathF.Abs(expected),
            $"The {what} is {actual}, not within {share:P0} of {expected}.");
    }
}
