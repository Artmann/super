using System.Numerics;

namespace Rendition.Gpu.Tests;

// A mirror faces the camera at the origin, so it reflects what's behind the camera, which is off the screen and so out of
// the screen-space trace's reach: only a reflection probe can show it. Behind the camera stands a wall that glows red or
// blue, and frames draw it while a probe is captured, so the mirror's colour tells which probe it shows and when that
// probe's picture was taken.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class ReflectionProbeGpuTests(GpuFixture gpu)
{
    // A few frames, for the exposure and the reflections' history to settle.
    private const int FramesBefore = 4;

    // One capture a frame, so a probe waits behind every probe in line before it.
    private const int MostFramesToCapture = 16;

    private static readonly Vector3 ProbePosition = new(0f, 0f, 2f);
    private static readonly Vector3 RoomMin = new(-4f, -4f, -2f);
    private static readonly Vector3 RoomMax = new(4f, 4f, 8f);

    [Fact]
    public void A_mirror_shows_what_its_probe_saw_and_the_sky_without_one()
    {
        int errors = gpu.ValidationErrors;
        using var scene = new Scene(gpu);
        try
        {
            var bare = Mirror(scene, scene.RedWall);
            Place(scene, Probe(RoomMin, RoomMax), scene.RedWall);
            var reflected = Mirror(scene, scene.RedWall);

            Assert.True(Redness(reflected) > 40f, $"The mirror is {TestPixels.Show(reflected)} with the red probe.");
            Assert.True(Redness(bare) < 10f, $"The mirror is {TestPixels.Show(bare)} with no probe.");
            Assert.Equal(errors, gpu.ValidationErrors);
        }
        finally
        {
            gpu.Renderer.Reflections.Clear();
        }
    }

    [Fact]
    public void An_updated_probe_shows_the_new_picture_and_no_frame_waits_for_the_GPU()
    {
        int errors = gpu.ValidationErrors;
        using var scene = new Scene(gpu);
        try
        {
            var probe = Place(scene, Probe(RoomMin, RoomMax), scene.RedWall);
            var before = Mirror(scene, scene.RedWall);

            int idles = gpu.Context.Waits.WaitIdleCount;
            int submits = gpu.Context.Waits.SubmitAndWaitCount;
            int captures = gpu.Renderer.Reflections.CapturesDrawn;
            gpu.Renderer.Reflections.Update(probe, Probe(RoomMin, RoomMax));
            Settle(scene, probe, scene.BlueWall);
            for (int frame = 0; frame < FramesBefore; frame++)
            {
                gpu.Renderer.Render(scene.View(), scene.Draws(scene.BlueWall));
            }

            Assert.Equal(idles, gpu.Context.Waits.WaitIdleCount);
            Assert.Equal(submits, gpu.Context.Waits.SubmitAndWaitCount);
            Assert.Equal(captures + 1, gpu.Renderer.Reflections.CapturesDrawn);

            var after = Mirror(scene, scene.BlueWall);
            Assert.True(Redness(before) > 40f, $"The mirror is {TestPixels.Show(before)} before the update.");
            Assert.True(Redness(after) < -40f, $"The mirror is {TestPixels.Show(after)} after the update.");
            Assert.Equal(errors, gpu.ValidationErrors);
        }
        finally
        {
            gpu.Renderer.Reflections.Clear();
        }
    }

    [Fact]
    public void A_probe_keeps_its_picture_when_the_array_grows_past_eight()
    {
        int errors = gpu.ValidationErrors;
        using var scene = new Scene(gpu);
        try
        {
            Place(scene, Probe(RoomMin, RoomMax), scene.RedWall);

            // Eight more, in small boxes off to the side, so the ninth probe needs a bigger array. The red probe isn't
            // captured again, so it keeps its picture only if the array's copy keeps it.
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(20f + 2f * i, 0f, 0f);
                Place(scene, new ReflectionProbe { Position = corner + Vector3.One * 0.5f, BoxMin = corner, BoxMax = corner + Vector3.One }, scene.BlueWall);
            }

            var reflected = Mirror(scene, scene.BlueWall);

            Assert.Equal(8, Rendition.Vulkan.ProbeCubes.FirstCapacity);
            Assert.Equal(9, gpu.Renderer.Reflections.Count);
            Assert.True(Redness(reflected) > 40f, $"The mirror is {TestPixels.Show(reflected)} after the array grew.");
            Assert.Equal(errors, gpu.ValidationErrors);
        }
        finally
        {
            gpu.Renderer.Reflections.Clear();
        }
    }

    [Fact]
    public void Where_boxes_overlap_the_higher_priority_shows_and_removing_it_leaves_the_other()
    {
        int errors = gpu.ValidationErrors;
        using var scene = new Scene(gpu);
        try
        {
            Place(scene, Probe(RoomMin, RoomMax), scene.RedWall);
            var blue = Place(scene, Probe(RoomMin, RoomMax) with { Priority = 1f }, scene.BlueWall);
            var both = Mirror(scene, scene.BlueWall);
            gpu.Renderer.Reflections.Remove(blue);
            var left = Mirror(scene, scene.BlueWall);

            Assert.True(Redness(both) < -40f, $"The mirror is {TestPixels.Show(both)} with the blue probe first.");
            Assert.True(Redness(left) > 40f, $"The mirror is {TestPixels.Show(left)} once the blue probe is removed.");
            Assert.Equal(1, gpu.Renderer.Reflections.Count);
            Assert.Equal(errors, gpu.ValidationErrors);
        }
        finally
        {
            gpu.Renderer.Reflections.Clear();
        }
    }

    [Fact]
    public void Between_equal_priorities_the_smaller_box_shows_whatever_order_they_came_in()
    {
        int errors = gpu.ValidationErrors;
        using var scene = new Scene(gpu);
        try
        {
            // The yard's probe comes first, then a room's inside it, which still holds the mirror and the probe.
            Place(scene, Probe(RoomMin * 2f, RoomMax * 2f), scene.RedWall);
            Place(scene, Probe(RoomMin, RoomMax), scene.BlueWall);
            var reflected = Mirror(scene, scene.RedWall);

            Assert.True(Redness(reflected) < -40f, $"The mirror is {TestPixels.Show(reflected)} with the room's blue probe inside the yard's red one.");
            Assert.Equal(errors, gpu.ValidationErrors);
        }
        finally
        {
            gpu.Renderer.Reflections.Clear();
        }
    }

    private static ReflectionProbe Probe(Vector3 min, Vector3 max) => new()
    {
        Position = ProbePosition,
        BoxMin = min,
        BoxMax = max,
        BlendDistance = 0.5f,
    };

    // Adds a probe and draws frames with the wall given until its picture is taken.
    private ReflectionProbeHandle Place(Scene scene, in ReflectionProbe probe, DrawItem[] wall)
    {
        var handle = gpu.Renderer.Reflections.Add(probe);
        Settle(scene, handle, wall);
        return handle;
    }

    private void Settle(Scene scene, ReflectionProbeHandle handle, DrawItem[] wall)
    {
        for (int frame = 0; frame < MostFramesToCapture && gpu.Renderer.Reflections.Waiting(handle); frame++)
        {
            gpu.Renderer.Render(scene.View(), scene.Draws(wall));
        }

        Assert.False(gpu.Renderer.Reflections.Waiting(handle), $"Probe {handle.Index} was still waiting for its picture.");
    }

    // The middle of the mirror, after a few frames with the wall given behind the camera.
    private Vector3 Mirror(Scene scene, DrawItem[] wall)
    {
        var view = scene.View();
        var draws = scene.Draws(wall);
        for (int frame = 0; frame < FramesBefore; frame++)
        {
            gpu.Renderer.Render(view, draws);
        }

        byte[] pixels = gpu.Renderer.Capture(view, draws, out _, out _);
        return TestPixels.Mean(pixels, 256, 128, 128, 16);
    }

    // How much redder than blue: above 0 for the red wall, below for the blue.
    private static float Redness(Vector3 colour) => colour.X - colour.Z;

    private sealed class Scene : IDisposable
    {
        private readonly GpuFixture _gpu;
        private readonly MeshHandle _mesh;
        private readonly MaterialHandle _red;
        private readonly MaterialHandle _blue;
        private readonly MaterialHandle _mirror;
        private readonly DrawItem _mirrorDraw;

        public Scene(GpuFixture gpu)
        {
            _gpu = gpu;
            var (vertices, indices) = TestMeshes.Quad();
            _mesh = gpu.Renderer.Assets.CreateMesh(vertices, indices);
            _red = Glow(new Vector3(2f, 0f, 0f));
            _blue = Glow(new Vector3(0f, 0f, 2f));
            _mirror = gpu.CreateMirror();

            var quad = new DrawItem { Mesh = _mesh, IndexCount = (uint)indices.Length, Transform = Matrix4x4.Identity };
            _mirrorDraw = quad with { Material = _mirror, Transform = Matrix4x4.CreateScale(2f) };

            // Behind the camera, turned to face it and the mirror.
            var wall = quad with
            {
                Transform = Matrix4x4.CreateScale(16f) * Matrix4x4.CreateRotationY(MathF.PI) * Matrix4x4.CreateTranslation(0f, 0f, 7.5f),
            };
            RedWall = [wall with { Material = _red }];
            BlueWall = [wall with { Material = _blue }];
        }

        public DrawItem[] RedWall { get; }
        public DrawItem[] BlueWall { get; }

        /// <summary>What a frame draws: the mirror, and behind the camera the wall given.</summary>
        public DrawItem[] Draws(DrawItem[] wall) => [_mirrorDraw, .. wall];

        public FrameView View()
        {
            var view = _gpu.View(DebugView.None);
            view.Off = RenderEffects.AntiAliasing | RenderEffects.LightProbes | RenderEffects.AutoExposure;
            view.Environment.Intensity = 1f;
            return view;
        }

        private MaterialHandle Glow(Vector3 emissive) => _gpu.Renderer.Assets.CreateMaterial(new Material
        {
            BaseColor = new Vector4(0f, 0f, 0f, 1f), Roughness = 1f, Emissive = emissive,
        });

        public void Dispose()
        {
            _gpu.Renderer.Assets.DestroyMesh(_mesh);
            _gpu.Renderer.Assets.DestroyMaterial(_red);
            _gpu.Renderer.Assets.DestroyMaterial(_blue);
            _gpu.Renderer.Assets.DestroyMaterial(_mirror);
        }
    }
}
