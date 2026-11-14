using System.Numerics;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// Haze and underwater have no pass of their own, and subsurface's blurs cost next to nothing at this size, so these check
// the picture. With the switch off, the frame must come out as it does without the effect set at all, and with it on the
// picture must visibly change.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class SwitchPictureGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    private const int Size = 256;

    // Half the width of the square averaged at the picture's centre, which is the far object.
    private const int SampleRadius = 24;

    // Two captures of the same frame differ by a step or two at most. Anything past this is a different picture.
    private const float SameTolerance = 4f;

    // Haze or water that moved the centre by less than this would not be seen, so the check would prove nothing.
    private const float VisibleChange = 40f;

    private static readonly Vector3 HazeColour = new(1f, 0f, 0f);

    private static readonly DirectionalLight[] Backlight =
    [
        new() { Direction = Vector3.Normalize(new Vector3(0f, 0.2f, -1f)), Radiance = new Vector3(6f) },
    ];

    private static readonly DirectionalLight[] Frontlight =
    [
        new() { Direction = Vector3.Normalize(new Vector3(0f, 0.2f, 1f)), Radiance = new Vector3(2f) },
    ];

    [Fact]
    public void Haze_off_leaves_a_far_object_as_it_is_with_no_haze()
    {
        WithScene(scene =>
        {
            // The scene has no light, so the object glows to be seen at all.
            var far = scene.Quad(
                new Material
                {
                    BaseColor = new Vector4(0.2f, 0.6f, 0.2f, 1f), Roughness = 0.8f,
                    Emissive = new Vector3(0.2f, 0.6f, 0.2f),
                },
                Matrix4x4.CreateScale(40f) * Matrix4x4.CreateTranslation(0f, 0f, -40f));

            var none = Centre(Render(View(haze: false), far));
            var hazed = Centre(Render(View(haze: true), far));
            var hazeOff = Centre(Render(View(haze: true, off: RenderEffects.Haze), far));
            output.WriteLine($"Far object centre: no haze {Show(none)}, haze on {Show(hazed)}, haze off {Show(hazeOff)}.");

            Assert.True(
                Distance(hazed, none) > VisibleChange,
                $"Haze changed the far object's centre by only {Distance(hazed, none):F1}, so the check proves nothing.");
            Assert.True(
                Distance(hazeOff, none) <= SameTolerance,
                $"With haze off the far object's centre was {Show(hazeOff)}, but with no haze it is {Show(none)}.");
            Assert.True(
                Distance(hazeOff, hazed) > VisibleChange,
                $"With haze off the far object's centre was {Show(hazeOff)}, the same as with haze on at {Show(hazed)}.");
        });
    }

    [Fact]
    public void Underwater_off_leaves_the_picture_as_it_is_with_no_water()
    {
        WithScene(scene =>
        {
            var wall = scene.Quad(
                new Material
                {
                    BaseColor = new Vector4(0.8f, 0.8f, 0.8f, 1f), Roughness = 0.8f,
                    Emissive = new Vector3(0.5f, 0.5f, 0.5f),
                },
                Matrix4x4.CreateScale(20f) * Matrix4x4.CreateTranslation(0f, 0f, -4f));
            var water = new UnderwaterSettings
            {
                Absorption = new Vector3(0.5f, 0.2f, 0.1f),
                ShallowColor = new Vector3(0.1f, 0.4f, 0.6f),
                DeepColor = new Vector3(0f, 0.1f, 0.2f),
                SurfaceHeight = 10f,
            };

            var dry = Centre(Render(View(), wall));
            var wet = Centre(Render(View(water: water), wall));
            var wetOff = Centre(Render(View(water: water, off: RenderEffects.Underwater), wall));
            output.WriteLine($"Wall centre: no water {Show(dry)}, underwater {Show(wet)}, underwater off {Show(wetOff)}.");

            Assert.True(
                Distance(wet, dry) > VisibleChange,
                $"Water changed the centre by only {Distance(wet, dry):F1}, so the check proves nothing.");
            Assert.True(
                Distance(wetOff, dry) <= SameTolerance,
                $"With underwater off the centre was {Show(wetOff)}, but with no water it is {Show(dry)}.");
        });
    }

    [Fact]
    public void Subsurface_off_lights_skin_as_the_same_plain_material()
    {
        WithScene(scene =>
        {
            var colour = new Vector4(0.85f, 0.6f, 0.48f, 1f);
            var at = Matrix4x4.CreateScale(4f);
            var skinWall = scene.Quad(
                new Material
                {
                    BaseColor = colour, Roughness = 0.5f,
                    Subsurface = new Subsurface { Radius = 0.02f, Color = new Vector3(1f, 0.4f, 0.25f), Transmission = 1f },
                },
                at);
            var plainWall = scene.Quad(new Material { BaseColor = colour, Roughness = 0.5f }, at);

            // With the sun behind the wall, only light that shines through it reaches the camera's side.
            var flat = Centre(Render(LitView(Backlight, RenderEffects.None), plainWall));
            var scattered = Centre(Render(LitView(Backlight, RenderEffects.None), skinWall));
            var scatterOff = Centre(Render(LitView(Backlight, RenderEffects.Subsurface), skinWall));
            output.WriteLine($"Backlit wall centre: plain {Show(flat)}, skin {Show(scattered)}, skin with subsurface off {Show(scatterOff)}.");

            // With the sun in front, a skin wall that still sent its light to be spread would lose it with the blurs skipped.
            var frontFlat = Centre(Render(LitView(Frontlight, RenderEffects.None), plainWall));
            var frontOff = Centre(Render(LitView(Frontlight, RenderEffects.Subsurface), skinWall));
            output.WriteLine($"Front-lit wall centre: plain {Show(frontFlat)}, skin with subsurface off {Show(frontOff)}.");

            Assert.True(
                Distance(scattered, flat) > VisibleChange,
                $"Subsurface changed the wall's centre by only {Distance(scattered, flat):F1}, so the check proves nothing.");
            Assert.True(
                Distance(scatterOff, flat) <= SameTolerance,
                $"With subsurface off the backlit skin wall's centre was {Show(scatterOff)}, but the plain wall's is {Show(flat)}.");
            Assert.True(
                Distance(frontOff, frontFlat) <= SameTolerance,
                $"With subsurface off the front-lit skin wall's centre was {Show(frontOff)}, but the plain wall's is {Show(frontFlat)}.");
        });
    }

    // Runs the body with a scene that destroys what it made, then checks the validation errors did not grow.
    private void WithScene(Action<Scene> body)
    {
        int errors = gpu.ValidationErrors;
        using (var scene = new Scene(gpu))
        {
            body(scene);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // The fixture's view lit by the sun given, with no shadow map, so the wall doesn't shade itself.
    private FrameView LitView(DirectionalLight[] sun, RenderEffects off)
    {
        var view = View(off: off | RenderEffects.SunShadows);
        view.Lights = sun;
        return view;
    }

    // The fixture's view with the effects that would add frame-to-frame noise left off. The light probes fill in over
    // many frames, and would keep this scene's light for the tests after it, so they are off too, which frees them.
    private FrameView View(bool haze = false, RenderEffects off = RenderEffects.None, UnderwaterSettings water = default)
    {
        var view = gpu.View(DebugView.None);
        view.Off = off | RenderEffects.AntiAliasing | RenderEffects.Reflections | RenderEffects.LightProbes;
        if (haze)
        {
            view.BackgroundFadeStart = 5f;
            view.BackgroundFadeEnd = 10f;
            view.BackgroundFadeColor = HazeColour;
        }

        view.Water = water;
        return view;
    }

    // A few frames first, then the one read back, so the picture has settled the same way for every capture.
    private byte[] Render(FrameView view, params DrawItem[] draws)
    {
        for (int frame = 0; frame < 2; frame++)
        {
            gpu.Renderer.Render(view, draws);
        }

        return gpu.Renderer.Capture(view, draws, out _, out _);
    }

    // The mean colour of the square at the picture's centre, in 0 to 255.
    private static Vector3 Centre(byte[] pixels)
    {
        var sum = Vector3.Zero;
        int count = 0;
        for (int y = Size / 2 - SampleRadius; y < Size / 2 + SampleRadius; y++)
        {
            for (int x = Size / 2 - SampleRadius; x < Size / 2 + SampleRadius; x++)
            {
                int at = (y * Size + x) * 4;
                sum += new Vector3(pixels[at], pixels[at + 1], pixels[at + 2]);
                count++;
            }
        }

        return sum / count;
    }

    private static float Distance(Vector3 a, Vector3 b) => Vector3.Distance(a, b);

    private static string Show(Vector3 colour) => $"({colour.X:F0}, {colour.Y:F0}, {colour.Z:F0})";

    // The materials and quads a test makes, destroyed together when the test ends.
    private sealed class Scene(GpuFixture gpu) : IDisposable
    {
        private readonly List<MeshHandle> meshes = [];
        private readonly List<MaterialHandle> materials = [];

        // A quad with the material, moved by the transform.
        public DrawItem Quad(Material material, Matrix4x4 transform)
        {
            var quad = TestMeshes.Quad();
            var handle = gpu.Renderer.CreateMaterial(material);
            materials.Add(handle);
            var item = gpu.Renderer.CreateDrawItem(quad.Vertices, quad.Indices, handle) with { Transform = transform };
            meshes.Add(item.Mesh);
            return item;
        }

        public void Dispose()
        {
            foreach (var mesh in meshes)
            {
                gpu.Renderer.DestroyMesh(mesh);
            }

            foreach (var material in materials)
            {
                gpu.Renderer.DestroyMaterial(material);
            }
        }
    }
}
