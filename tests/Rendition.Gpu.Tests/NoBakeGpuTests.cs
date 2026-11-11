using System.Numerics;

namespace Rendition.Gpu.Tests;

// A frame that names a baked space and chunks but has no bake must look exactly as it would without them. Every effect
// is off, so nothing carried from frame to frame (anti-aliasing's history, exposure) makes two captures differ, and
// each capture lands on the same frame of the dither's cycle.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class NoBakeGpuTests(GpuFixture gpu)
{
    private const int Size = 256;
    private const int NoisePeriod = 64;

    private static readonly BakedSpace Room = new("room", new Vector3(-3f, -3f, -3f), new Vector3(3f, 3f, 3f), 1f);
    private static readonly BakedChunk[] Shed =
        [new("shed", Matrix4x4.CreateTranslation(20f, 0f, 0f), new Vector3(-2f, 0f, -2f), new Vector3(2f, 3f, 2f))];

    [Fact]
    public void Baked_space_without_a_bake_renders_the_same_bytes()
    {
        int errors = gpu.ValidationErrors;
        var cube = Cube();
        try
        {
            byte[] plain = Capture(cube, LightMode.Mixed, baked: false);
            byte[] baked = Capture(cube, LightMode.Baked, baked: true);
            byte[] plainAgain = Capture(cube, LightMode.Mixed, baked: false);

            // The plain frame repeats, so a difference below can only come from the bake.
            Assert.Equal(plain, plainAgain);
            Assert.Equal(plain, baked);
        }
        finally
        {
            gpu.Renderer.DestroyMesh(cube.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Renderer_without_a_store_drops_no_light()
    {
        int errors = gpu.ValidationErrors;
        var cube = Cube();
        try
        {
            Capture(cube, LightMode.Baked, baked: true);

            var frame = Assert.IsType<BakeFrame>(gpu.Renderer.LastBakeFrame);
            Assert.NotNull(frame.Space);
            Assert.False(frame.SpaceFresh);
            Assert.Empty(frame.DroppedLocalLights);
            Assert.Empty(frame.DroppedSuns);
            Assert.Equal(LightMode.Realtime, frame.ModeOf(new LightKey(false, 1)));
        }
        finally
        {
            gpu.Renderer.DestroyMesh(cube.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Frame_without_baked_areas_leaves_no_bake_frame()
    {
        int errors = gpu.ValidationErrors;
        var cube = Cube();
        try
        {
            Capture(cube, LightMode.Baked, baked: true);
            Assert.NotNull(gpu.Renderer.LastBakeFrame);

            Capture(cube, LightMode.Baked, baked: false);

            Assert.Null(gpu.Renderer.LastBakeFrame);
        }
        finally
        {
            gpu.Renderer.DestroyMesh(cube.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Fresh_bake_draws_the_frame_without_its_baked_light()
    {
        int errors = gpu.ValidationErrors;
        var cube = Cube();
        try
        {
            byte[] unlit = Capture(cube, LightMode.Mixed, baked: false, withPointLight: false);
            byte[] lit = Capture(cube, LightMode.Mixed, baked: false);
            gpu.Renderer.Bakes = new EveryBakeFresh();
            byte[] dropped = Capture(cube, LightMode.Baked, baked: true);
            // Twice, so the array the kept lights are copied into is reused.
            byte[] droppedAgain = Capture(cube, LightMode.Baked, baked: true);

            Assert.NotEqual(unlit, lit);
            Assert.Equal([0], gpu.Renderer.LastBakeFrame!.DroppedLocalLights);
            Assert.Equal(unlit, dropped);
            Assert.Equal(unlit, droppedAgain);
        }
        finally
        {
            gpu.Renderer.Bakes = NoBakes.Instance;
            gpu.Renderer.DestroyMesh(cube.Mesh);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private DrawItem Cube()
    {
        var (vertices, indices) = TestMeshes.Cube();
        return gpu.Renderer.CreateDrawItem(vertices, indices, gpu.Matte) with
        {
            Transform = Matrix4x4.CreateRotationY(0.5f) * Matrix4x4.CreateRotationX(0.3f),
        };
    }

    // A cube under a Mixed sun and a point light in front of it. Baked, the frame names a space round the cube and a
    // chunk off to the side, the cube is static, and the point light takes the mode given.
    private byte[] Capture(DrawItem cube, LightMode pointMode, bool baked, bool withPointLight = true)
    {
        DirectionalLight[] suns =
        [
            new() { Direction = Vector3.Normalize(new Vector3(0.4f, 1f, 0.6f)), Radiance = new Vector3(1.5f, 1.4f, 1.2f) },
        ];
        LocalLight[] points =
        [
            new() { Mode = pointMode, Key = 1, Position = new Vector3(0.6f, 0.4f, 1.6f), Radiance = new Vector3(3f, 2f, 1f), Range = 6f },
        ];
        var draw = baked ? cube with { BakeId = BakeIds.Of(1, 0) } : cube;
        var view = new FrameView
        {
            Width = Size,
            Height = Size,
            View = Matrix4x4.CreateLookAt(new Vector3(0, 0, 3), Vector3.Zero, Vector3.UnitY),
            Projection = Projection.PerspectiveInfiniteReversedZ(MathF.PI / 4f, 1f, 0.1f),
            CameraPosition = new Vector3(0, 0, 3),
            Lights = suns,
            LocalLights = withPointLight ? points : [],
            Off = RenderEffects.All,
            BakedSpace = baked ? Room : null,
            BakedChunks = baked ? Shed : [],
        };

        // The dither's noise repeats every 64 frames, so each capture is the 64th frame of its own, and two captures
        // in one test fall on the same noise.
        for (int i = 0; i < NoisePeriod - 1; i++)
        {
            gpu.Renderer.Render(view, [draw]);
        }

        return gpu.Renderer.Capture(view, [draw], out _, out _);
    }

    private sealed class EveryBakeFresh : IBakeStore
    {
        public bool IsFresh(string file, BakeInputs inputs) => true;
    }
}
