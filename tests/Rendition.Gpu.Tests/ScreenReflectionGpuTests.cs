using System.Numerics;

namespace Rendition.Gpu.Tests;

// A wall that glows red stands a metre behind the origin, and a mirror floor runs from its foot towards the camera. The
// floor has nothing red of its own, so any red on it is the wall seen in it. The corner between them is on line 166 of
// 256, and the lines just below it are the floor nearest the wall.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class ScreenReflectionGpuTests(GpuFixture gpu)
{
    private const int FirstFloorLine = 174;
    private const int LastFloorLine = 190;

    // The trace reads last frame's picture, so it needs a frame before it, and a few more for the light probes' and the
    // exposure's first steps to pass.
    private const int FramesBefore = 6;

    [Theory]
    [InlineData(ReflectionResolution.Half)]
    [InlineData(ReflectionResolution.Full)]
    public void A_mirror_floor_shows_the_red_wall_behind_it_only_with_reflections_on(ReflectionResolution resolution)
    {
        int errors = gpu.ValidationErrors;
        using var scene = new Scene(gpu);

        float reflected = Redness(Capture(scene.Draws, resolution, RenderEffects.None));
        float unreflected = Redness(Capture(scene.Draws, resolution, RenderEffects.Reflections));

        Assert.True(reflected > 40f, $"The floor is {reflected} of 255 redder than it is green with reflections on.");
        Assert.True(unreflected < reflected * 0.5f,
            $"The floor is {unreflected} of 255 redder than it is green with reflections off, and {reflected} with them on.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Half_resolution_reflects_as_much_of_the_wall_as_full()
    {
        int errors = gpu.ValidationErrors;
        using var scene = new Scene(gpu);

        float half = Redness(Capture(scene.Draws, ReflectionResolution.Half, RenderEffects.None));
        float full = Redness(Capture(scene.Draws, ReflectionResolution.Full, RenderEffects.None));

        Assert.True(full > 40f, $"The floor is {full} of 255 redder than it is green at full resolution.");
        Assert.True(MathF.Abs(half - full) < full * 0.05f, $"The floor's redness is {half} at half resolution and {full} at full.");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // A floor pixel between two traced ones takes its reflection from them, so every pixel of a line has one: no pixel
    // of a line may be left with much less of the wall than the line has on the whole.
    [Fact]
    public void At_half_resolution_no_floor_pixel_is_left_without_a_reflection()
    {
        int errors = gpu.ValidationErrors;
        using var scene = new Scene(gpu);

        byte[] pixels = Capture(scene.Draws, ReflectionResolution.Half, RenderEffects.None);

        for (int line = FirstFloorLine; line <= LastFloorLine; line++)
        {
            float mean = 0f;
            float least = float.MaxValue;
            for (int x = 64; x < 192; x++)
            {
                float redness = Redness(pixels, x, line);
                mean += redness / 128f;
                least = MathF.Min(least, redness);
            }

            Assert.True(mean > 40f, $"Line {line} is {mean} of 255 redder than it is green.");
            Assert.True(least > mean * 0.8f, $"Line {line} has a pixel {least} redder than green, where the line's mean is {mean}.");
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private byte[] Capture(DrawItem[] draws, ReflectionResolution resolution, RenderEffects off)
    {
        var view = gpu.View(DebugView.None);
        // No anti-aliasing, so the frame captured is the frame traced, and no light probes or exposure, which would
        // still be settling.
        view.Off = off | RenderEffects.AntiAliasing | RenderEffects.LightProbes | RenderEffects.AutoExposure;
        view.EnvironmentIntensity = 1f;
        gpu.Renderer.ReflectionResolution = resolution;
        try
        {
            for (int frame = 0; frame < FramesBefore; frame++)
            {
                gpu.Renderer.Render(view, draws);
            }

            return gpu.Renderer.Capture(view, draws, out _, out _);
        }
        finally
        {
            gpu.Renderer.ReflectionResolution = ReflectionResolution.Half;
        }
    }

    // How much redder than green the floor nearest the wall is, on average, clear of the picture's edges.
    private static float Redness(byte[] pixels)
    {
        float sum = 0f;
        int count = 0;
        for (int line = FirstFloorLine; line <= LastFloorLine; line++)
        {
            for (int x = 64; x < 192; x++)
            {
                sum += Redness(pixels, x, line);
                count++;
            }
        }

        return sum / count;
    }

    private static float Redness(byte[] pixels, int x, int line)
    {
        int at = (line * 256 + x) * 4;
        return pixels[at] - pixels[at + 1];
    }

    private sealed class Scene : IDisposable
    {
        private readonly GpuFixture _gpu;
        private readonly MeshHandle _mesh;
        private readonly MaterialHandle _glow;
        private readonly MaterialHandle _mirror;

        public Scene(GpuFixture gpu)
        {
            _gpu = gpu;
            var (vertices, indices) = TestMeshes.Quad();
            _mesh = gpu.Renderer.CreateMesh(vertices, indices);
            _glow = gpu.Renderer.CreateMaterial(new Material
            {
                BaseColor = new Vector4(0f, 0f, 0f, 1f), Roughness = 1f, Emissive = new Vector3(1f, 0f, 0f),
            });
            _mirror = gpu.Renderer.CreateMaterial(new Material
            {
                BaseColor = new Vector4(1f, 1f, 1f, 1f), Metallic = 1f, Roughness = 0.05f,
            });
            var quad = new DrawItem { Mesh = _mesh, IndexCount = (uint)indices.Length, Transform = Matrix4x4.Identity };
            Draws =
            [
                quad with { Material = _glow, Transform = Matrix4x4.CreateScale(4f) * Matrix4x4.CreateTranslation(0f, 0f, -1f) },
                quad with
                {
                    Material = _mirror,
                    Transform = Matrix4x4.CreateScale(4f) * Matrix4x4.CreateRotationX(-MathF.PI / 2f)
                        * Matrix4x4.CreateTranslation(0f, -0.5f, 1f),
                },
            ];
        }

        public DrawItem[] Draws { get; }

        public void Dispose()
        {
            _gpu.Renderer.DestroyMesh(_mesh);
            _gpu.Renderer.DestroyMaterial(_glow);
            _gpu.Renderer.DestroyMaterial(_mirror);
        }
    }
}
