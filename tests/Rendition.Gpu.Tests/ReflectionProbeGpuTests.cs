using System.Numerics;

namespace Rendition.Gpu.Tests;

// A mirror faces the camera at the origin, so it reflects what's behind the camera, which is off the screen and so out of
// the screen-space trace's reach: only a reflection probe can show it. Behind the camera, a wall that glows red or blue is
// drawn only into the probes' captures, never into the frame, so the mirror's colour tells which probe it shows.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class ReflectionProbeGpuTests(GpuFixture gpu)
{
    // A few frames, for the exposure and the reflections' history to settle.
    private const int FramesBefore = 4;

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
            var bare = Mirror(scene);
            scene.Capture(Probe(RoomMin, RoomMax), scene.RedWall);
            var reflected = Mirror(scene);

            Assert.True(Redness(reflected) > 40f, $"The mirror is {TestPixels.Show(reflected)} with the red probe.");
            Assert.True(Redness(bare) < 10f, $"The mirror is {TestPixels.Show(bare)} with no probe.");
            Assert.Equal(errors, gpu.ValidationErrors);
        }
        finally
        {
            gpu.Renderer.ClearReflectionProbes();
        }
    }

    [Fact]
    public void A_probe_keeps_its_picture_when_the_array_grows_past_eight()
    {
        int errors = gpu.ValidationErrors;
        using var scene = new Scene(gpu);
        try
        {
            scene.Capture(Probe(RoomMin, RoomMax), scene.RedWall);

            // Eight more, in small boxes off to the side, so the ninth probe needs a bigger array.
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(20f + 2f * i, 0f, 0f);
                scene.Capture(new ReflectionProbe { Position = corner + Vector3.One * 0.5f, BoxMin = corner, BoxMax = corner + Vector3.One }, scene.BlueWall);
            }

            var reflected = Mirror(scene);

            Assert.Equal(8, Rendition.Vulkan.ProbeCubes.FirstCapacity);
            Assert.Equal(9, gpu.Renderer.ReflectionProbeCount);
            Assert.True(Redness(reflected) > 40f, $"The mirror is {TestPixels.Show(reflected)} after the array grew.");
            Assert.Equal(errors, gpu.ValidationErrors);
        }
        finally
        {
            gpu.Renderer.ClearReflectionProbes();
        }
    }

    [Fact]
    public void Where_boxes_overlap_the_higher_priority_shows_and_removing_it_leaves_the_other()
    {
        int errors = gpu.ValidationErrors;
        using var scene = new Scene(gpu);
        try
        {
            scene.Capture(Probe(RoomMin, RoomMax), scene.RedWall);
            var blue = scene.Capture(Probe(RoomMin, RoomMax) with { Priority = 1f }, scene.BlueWall);
            var both = Mirror(scene);
            gpu.Renderer.RemoveReflectionProbe(blue);
            var left = Mirror(scene);

            Assert.True(Redness(both) < -40f, $"The mirror is {TestPixels.Show(both)} with the blue probe first.");
            Assert.True(Redness(left) > 40f, $"The mirror is {TestPixels.Show(left)} once the blue probe is removed.");
            Assert.Equal(1, gpu.Renderer.ReflectionProbeCount);
            Assert.Equal(errors, gpu.ValidationErrors);
        }
        finally
        {
            gpu.Renderer.ClearReflectionProbes();
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
            scene.Capture(Probe(RoomMin * 2f, RoomMax * 2f), scene.RedWall);
            scene.Capture(Probe(RoomMin, RoomMax), scene.BlueWall);
            var reflected = Mirror(scene);

            Assert.True(Redness(reflected) < -40f, $"The mirror is {TestPixels.Show(reflected)} with the room's blue probe inside the yard's red one.");
            Assert.Equal(errors, gpu.ValidationErrors);
        }
        finally
        {
            gpu.Renderer.ClearReflectionProbes();
        }
    }

    private static ReflectionProbe Probe(Vector3 min, Vector3 max) => new()
    {
        Position = ProbePosition,
        BoxMin = min,
        BoxMax = max,
        BlendDistance = 0.5f,
    };

    // The middle of the mirror, after a few frames.
    private Vector3 Mirror(Scene scene)
    {
        var view = scene.View();
        for (int frame = 0; frame < FramesBefore; frame++)
        {
            gpu.Renderer.Render(view, scene.Frame);
        }

        byte[] pixels = gpu.Renderer.Capture(view, scene.Frame, out _, out _);
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

        public Scene(GpuFixture gpu)
        {
            _gpu = gpu;
            var (vertices, indices) = TestMeshes.Quad();
            _mesh = gpu.Renderer.CreateMesh(vertices, indices);
            _red = Glow(new Vector3(2f, 0f, 0f));
            _blue = Glow(new Vector3(0f, 0f, 2f));
            _mirror = gpu.CreateMirror();

            var quad = new DrawItem { Mesh = _mesh, IndexCount = (uint)indices.Length, Transform = Matrix4x4.Identity };
            Frame = [quad with { Material = _mirror, Transform = Matrix4x4.CreateScale(2f) }];

            // Behind the camera, turned to face it and the mirror.
            var wall = quad with
            {
                Transform = Matrix4x4.CreateScale(16f) * Matrix4x4.CreateRotationY(MathF.PI) * Matrix4x4.CreateTranslation(0f, 0f, 7.5f),
            };
            RedWall = [wall with { Material = _red }];
            BlueWall = [wall with { Material = _blue }];
        }

        /// <summary>What every frame draws: the mirror alone.</summary>
        public DrawItem[] Frame { get; }

        public DrawItem[] RedWall { get; }
        public DrawItem[] BlueWall { get; }

        public FrameView View()
        {
            var view = _gpu.View(DebugView.None);
            view.Off = RenderEffects.AntiAliasing | RenderEffects.LightProbes | RenderEffects.AutoExposure;
            view.EnvironmentIntensity = 1f;
            return view;
        }

        public ReflectionProbeHandle Capture(in ReflectionProbe probe, DrawItem[] wall) =>
            _gpu.Renderer.CaptureReflectionProbe(probe, View(), wall, wall);

        private MaterialHandle Glow(Vector3 emissive) => _gpu.Renderer.CreateMaterial(new Material
        {
            BaseColor = new Vector4(0f, 0f, 0f, 1f), Roughness = 1f, Emissive = emissive,
        });

        public void Dispose()
        {
            _gpu.Renderer.DestroyMesh(_mesh);
            _gpu.Renderer.DestroyMaterial(_red);
            _gpu.Renderer.DestroyMaterial(_blue);
            _gpu.Renderer.DestroyMaterial(_mirror);
        }
    }
}
