using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition.Gpu.Tests;

/// <summary>
/// The baker's path tracer, through <see cref="Renderer.Bake"/>: small scenes whose light can be worked out by hand,
/// read back from the <c>.lightbake</c> file it returns.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class BakeTraceGpuTests(GpuFixture gpu)
{
    // The renderer's usual environment, put back after a test that changes it.
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

    private static readonly BakedSpace Room = new("trace-room", new Vector3(-6f, -3f, -6f), new Vector3(6f, 7f, 6f), 1f);

    // The test quad turned to face up, 2 metres square, so its uv (u, v) lies at (2u - 1, 0, 1 - 2v).
    private static Matrix4x4 Floor(float size = 2f, float height = 0f) =>
        Matrix4x4.CreateScale(size) * Matrix4x4.CreateRotationX(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, height, 0f);

    [Fact]
    public void A_plane_under_a_uniform_sky_reads_pi_L()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            gpu.Renderer.SetEnvironment(EvenSky());
            var draws = new[] { StaticDraw(quad, Floor(), gpu.Matte, 1) };

            var file = Bake(draws, [], sky: 1f, new BakeSettings(20, BakeQuality.Preview));

            // Every ray from the plane reaches a sky of L = 0.5, so the irradiance is the integral of L cos over the
            // hemisphere, pi * L = 1.5708.
            var centre = Irradiance(file, draws[0], 0.5f, 0.5f);
            AssertWithin(MathF.PI * 0.5f, centre.X, 0.05f, "irradiance under the sky");
        }
        finally
        {
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_closed_box_reads_black()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            gpu.Renderer.SetEnvironment(EvenSky());

            // Six faces 3.3 metres square, each turned to face into the box and set 1.6 metres out, so they overlap at
            // the edges and leave no crack. The control plane sits above the box under open sky.
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
            var draws = new List<DrawItem>();
            for (int i = 0; i < faces.Length; i++)
            {
                draws.Add(StaticDraw(quad, Matrix4x4.CreateScale(side) * faces[i], gpu.Matte, 10 + (ulong)i));
            }

            draws.Add(StaticDraw(quad, Floor(height: 5f), gpu.Matte, 20));
            var all = draws.ToArray();

            var file = Bake(all, [], sky: 1f, new BakeSettings(8, BakeQuality.Preview));

            // No ray from inside reaches the sky and nothing in the box gives light, so the floor is black.
            var inside = Irradiance(file, all[0], 0.5f, 0.5f);
            Assert.True(inside.X < 1e-3f && inside.Y < 1e-3f && inside.Z < 1e-3f, $"The box's floor reads {inside}, not black.");

            // Outside, the same sky gives pi * 0.5 = 1.5708, so the sky was on.
            var outside = Irradiance(file, all[^1], 0.5f, 0.5f);
            AssertWithin(MathF.PI * 0.5f, outside.X, 0.05f, "irradiance on the control plane");
        }
        finally
        {
            gpu.Renderer.SetEnvironment(DefaultEnvironment());
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void The_mask_is_zero_behind_a_wall_and_one_in_front()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var twoSided = gpu.Renderer.CreateMaterial(new Material
        {
            BaseColor = new Vector4(0.5f, 0.5f, 0.5f, 1f), Roughness = 0.8f, Flags = MaterialFlags.TwoSided,
        });
        try
        {
            // A floor 4 metres square, so texel i of 16 has its centre at x = (i + 0.5) / 4 - 2, and a wall across
            // x = 1 from 2 metres below the floor to 2 above, facing the light at (0, 1, 0).
            var floor = StaticDraw(quad, Floor(size: 4f), gpu.Matte, 30);
            var wall = StaticDraw(quad,
                Matrix4x4.CreateScale(4f) * Matrix4x4.CreateRotationY(-MathF.PI / 2f) * Matrix4x4.CreateTranslation(1f, 0f, 0f),
                twoSided, 31);
            var light = new LocalLight
            {
                Mode = LightMode.Mixed, Key = 1103, Position = new Vector3(0f, 1f, 0f), Radiance = new Vector3(5f),
                Range = 10f,
            };

            var file = Bake([floor, wall], [light], sky: 0f, new BakeSettings(4, BakeQuality.Preview));

            Assert.Single(file.Channels);
            Assert.Equal(1103, file.Channels[0].LightKey);
            int channel = file.Channels[0].Channel;
            var rect = RectOf(file, floor);
            var (x0, y0, size) = Texels(rect);
            Assert.Equal(16, size);

            // In front, at x = -0.875, the whole light is seen. Behind, from x = 1.625 on, the line to any point of the
            // light (radius 0.1 at height 1) crosses x = 1 below y = 1.1, inside the wall, so none of it is.
            for (int v = 4; v <= 11; v++)
            {
                byte front = MaskAt(file, rect.Page, x0 + 4, y0 + v, channel);
                Assert.True(front >= 250, $"The mask in front of the wall, row {v}, is {front}, not 255.");
                for (int u = 14; u < 16; u++)
                {
                    byte behind = MaskAt(file, rect.Page, x0 + u, y0 + v, channel);
                    Assert.True(behind <= 5, $"The mask behind the wall at texel ({u}, {v}) is {behind}, not 0.");
                }
            }
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
            gpu.Renderer.DestroyMaterial(twoSided);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_mixed_light_bounces_but_its_direct_light_is_not_baked()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var black = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0f, 0f, 0f, 1f), Roughness = 0.8f });
        try
        {
            var draws = LampUnderCeiling(quad, black);

            var file = Bake(draws, [Lamp(LightMode.Mixed, 1104)], sky: 0f, new BakeSettings(8, BakeQuality.Final));

            // The floor is black, so all its light comes off the ceiling. Straight above the lamp, with I = 10 at h = 1
            // below the ceiling, the ceiling gets E(r) = I h / (h^2 + r^2)^1.5 and gives back 0.5 E / pi. The floor,
            // H = 2 below, then gets the integral of 0.5 E(r) / pi * H^2 / (H^2 + r^2)^2 * 2 pi r dr, which comes to
            // 0.5 I (1 - 4 pi / (9 sqrt 3)) = 0.969 for an endless ceiling. The 10 metre ceiling, the lamp's 0.1 radius
            // and the average over 3 x 3 texels a metre square each move it by under 0.5%.
            float bounce = AverageAroundLamp(file, draws[0]);
            AssertWithin(0.5f * 10f * (1f - 4f * MathF.PI / (9f * MathF.Sqrt(3f))), bounce, 0.05f, "bounced light");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
            gpu.Renderer.DestroyMaterial(black);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_baked_light_is_baked_in_full()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var black = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0f, 0f, 0f, 1f), Roughness = 0.8f });
        try
        {
            var draws = LampUnderCeiling(quad, black);
            var settings = new BakeSettings(8, BakeQuality.Preview);

            // The same seed in both, so the bounce is the same and only the direct light differs. Each lamp has its own
            // key, since one key changing mode between bakes looks like a light that moved, which is left out.
            var mixed = Bake(draws, [Lamp(LightMode.Mixed, 1105)], sky: 0f, settings);
            var baked = Bake(draws, [Lamp(LightMode.Baked, 1106)], sky: 0f, settings);

            // Straight below the lamp, 1 metre down, a disc of radius R = 0.1 facing the floor gives
            // I * (2 / R^2) (sqrt(1 + R^2) - 1) = 10 * 0.99751 = 9.9751: the cosine averaged over the disc.
            var floor = draws[0];
            float direct = Irradiance(baked, floor, LampU, LampV).X - Irradiance(mixed, floor, LampU, LampV).X;
            AssertWithin(10f * 2f / 0.01f * (MathF.Sqrt(1.01f) - 1f), direct, 0.01f, "direct light of the Baked lamp");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
            gpu.Renderer.DestroyMaterial(black);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_moving_light_is_left_out_and_the_bake_stays_fresh()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            var draws = new[] { StaticDraw(quad, Floor(), gpu.Matte, 50) };
            var lamp = new LocalLight
            {
                Mode = LightMode.Baked, Key = 1107, Position = new Vector3(0f, 1f, 0f), Radiance = new Vector3(10f),
                Range = 10f,
            };

            // The lamp moves between the first two frames, so it is still settling when the bake is made.
            Render(draws, lamp, time: 0f);
            lamp.Position = new Vector3(0.5f, 1f, 0f);
            Render(draws, lamp, time: 0.1f);
            var view = gpu.View(DebugView.None);
            view.BakedSpace = Room;
            view.Time = 0.1f;
            view.LocalLights = new[] { lamp };
            byte[] bytes = gpu.Renderer.Bake(view, draws, new BakeSettings(20, BakeQuality.Preview));
            var file = LightBakeFile.Read(bytes);

            // A Baked lamp 10 bright a metre above would give the floor about 10; left out, nothing lights it.
            var centre = Irradiance(file, draws[0], 0.5f, 0.5f);
            Assert.True(centre.X < 1e-3f, $"The floor reads {centre.X}, so the moving lamp was baked.");

            // The next frame, with the lamp still settling, finds the bake fresh and keeps the lamp real-time.
            var inputs = LightBakeFile.ReadInputs(bytes);
            gpu.Renderer.Bakes = new StoreOf(inputs);
            Render(draws, lamp, time: 0.2f);
            var frame = gpu.Renderer.LastBakeFrame;
            Assert.NotNull(frame);
            Assert.True(frame.SpaceFresh, "The bake was stale on the next frame.");
            Assert.Equal(LightMode.Realtime, frame.ModeOf(new LightKey(false, 1107)));
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Light_through_a_half_opaque_pane_is_weakened()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        var glass = gpu.Renderer.CreateMaterial(new Material
        {
            BaseColor = new Vector4(1f, 1f, 1f, 0.5f), Roughness = 0.1f, Flags = MaterialFlags.Transparent,
        });
        try
        {
            var floor = StaticDraw(quad, Floor(), gpu.Matte, 60);
            var pane = StaticDraw(quad, Floor(size: 10f, height: 0.5f), glass, 61);
            var lamp = Lamp(LightMode.Baked, 1108);
            var settings = new BakeSettings(8, BakeQuality.Preview);

            var open = Bake([floor], [lamp], sky: 0f, settings);
            var covered = Bake([floor, pane], [lamp], sky: 0f, settings);

            // With no sky and nothing to bounce off, the floor has only the lamp's direct light. A pane half opaque
            // lets 1 - 0.5 of it through, and the same seed draws the same points on the lamp both times.
            float ratio = Irradiance(covered, floor, LampU, LampV).X / Irradiance(open, floor, LampU, LampV).X;
            AssertWithin(0.5f, ratio, 0.01f, "share of the light through the pane");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
            gpu.Renderer.DestroyMaterial(glass);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Bake_without_a_space_throws()
    {
        int errors = gpu.ValidationErrors;
        var error = Assert.Throws<InvalidOperationException>(
            () => gpu.Renderer.Bake(gpu.View(DebugView.None), [], new BakeSettings()));

        Assert.Equal("Bake needs a baked space in the frame; call view.BakedLighting first.", error.Message);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // The lamp sits over the centre of floor texel (8, 8) of 16, at tpm 8 on the 2 metre floor: uv 8.5 / 16.
    private const float LampU = 8.5f / 16f;
    private const float LampV = 8.5f / 16f;

    private static LocalLight Lamp(LightMode mode, int key) => new()
    {
        Mode = mode,
        Key = key,
        Position = new Vector3(2f * LampU - 1f, 1f, 1f - 2f * LampV),
        Radiance = new Vector3(10f),
        Range = 100f,
        Radius = 0.1f,
    };

    // A black floor 2 metres square and a grey ceiling 10 metres square, 2 metres above it, facing down.
    private DrawItem[] LampUnderCeiling(MeshHandle quad, MaterialHandle black) =>
    [
        StaticDraw(quad, Floor(), black, 40),
        StaticDraw(quad, Matrix4x4.CreateScale(10f) * Matrix4x4.CreateRotationX(MathF.PI / 2f) * Matrix4x4.CreateTranslation(0f, 2f, 0f),
            gpu.Matte, 41),
    ];

    // The mean irradiance of the 3 x 3 texels round the one under the lamp.
    private static float AverageAroundLamp(LightBakeFile file, DrawItem floor)
    {
        var rect = RectOf(file, floor);
        var (x0, y0, size) = Texels(rect);
        Assert.Equal(16, size);
        float sum = 0f;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                sum += IrradianceAt(file, rect.Page, x0 + 8 + dx, y0 + 8 + dy).X;
            }
        }

        return sum / 9f;
    }

    private LightBakeFile Bake(DrawItem[] draws, LocalLight[] lights, float sky, BakeSettings settings)
    {
        var view = gpu.View(DebugView.None);
        view.BakedSpace = Room;
        view.EnvironmentIntensity = sky;
        view.LocalLights = lights;
        return LightBakeFile.Read(gpu.Renderer.Bake(view, draws, settings));
    }

    private void Render(DrawItem[] draws, LocalLight lamp, float time)
    {
        var view = gpu.View(DebugView.None);
        view.BakedSpace = Room;
        view.Time = time;
        view.LocalLights = new[] { lamp };
        gpu.Renderer.Render(view, draws);
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

    private static LightmapRect RectOf(LightBakeFile file, DrawItem draw)
    {
        foreach (var (bakeId, rect) in file.Draws)
        {
            if (bakeId == draw.BakeId)
            {
                return rect;
            }
        }

        throw new InvalidOperationException($"The bake has no rectangle for draw {draw.BakeId}.");
    }

    private static (int X, int Y, int Side) Texels(LightmapRect rect) => (
        (int)MathF.Round(rect.Offset.X * LightmapLayout.PageSize),
        (int)MathF.Round(rect.Offset.Y * LightmapLayout.PageSize),
        (int)MathF.Round(rect.Scale.X * LightmapLayout.PageSize));

    // The irradiance of the texel under the draw's uv.
    private static Vector3 Irradiance(LightBakeFile file, DrawItem draw, float u, float v)
    {
        var rect = RectOf(file, draw);
        var (x0, y0, size) = Texels(rect);
        return IrradianceAt(file, rect.Page, x0 + (int)(u * size), y0 + (int)(v * size));
    }

    private static Vector3 IrradianceAt(LightBakeFile file, int page, int x, int y)
    {
        var halves = MemoryMarshal.Cast<byte, Half>(file.Irradiance[page].Pixels.AsSpan());
        int at = (y * LightmapLayout.PageSize + x) * 4;
        return new Vector3((float)halves[at], (float)halves[at + 1], (float)halves[at + 2]);
    }

    private static byte MaskAt(LightBakeFile file, int page, int x, int y, int channel) =>
        file.Mask[page].Pixels[(y * LightmapLayout.PageSize + x) * 4 + channel];

    private static void AssertWithin(float expected, float actual, float share, string what)
    {
        Assert.True(MathF.Abs(actual - expected) <= share * MathF.Abs(expected),
            $"The {what} is {actual}, not within {share:P0} of {expected}.");
    }

    private sealed class StoreOf(BakeInputs inputs) : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs asked) => asked == inputs;
    }
}
