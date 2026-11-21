using System.Numerics;
using static Rendition.Gpu.Tests.TestPixels;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// Grass is scattered on the GPU over its ground, so these check the picture: a grey floor seen from above turns green
// where blades stand on it, and stays grey with the switch off or the density at 0.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class GrassGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    private const int Size = 256;

    // Half the width of the square averaged at the picture's centre, which lands on the floor a few metres ahead.
    private const int SampleRadius = 24;

    // Two captures of the same frame differ by a step or two at most.
    private const float SameTolerance = 4f;

    // A few frames, so the scatter's counts and the history have settled.
    private const int Frames = 6;

    private static readonly Vector3 Eye = new(0f, 1.5f, 3f);

    private static readonly DirectionalLight[] Sun =
    [
        new() { Direction = Vector3.Normalize(new Vector3(0.3f, 1f, 0.4f)), Radiance = new Vector3(3f) },
    ];

    [Fact]
    public void Grass_on_a_grey_floor_turns_it_green()
    {
        WithFloor(floor =>
        {
            var bare = Centre(Render(View([]), floor));
            var grown = Centre(Render(View([new GrassDraw(floor, new GrassSettings())]), floor));
            output.WriteLine($"Centre: bare {Show(bare)}, with grass {Show(grown)}.");

            Assert.True(Brightness(bare) > 40f, $"The bare floor's centre is only {Show(bare)}, so the check proves nothing.");
            Assert.True(MathF.Abs(bare.Y - bare.X) < 8f, $"The bare grey floor's centre was {Show(bare)}, already coloured.");
            Assert.True(
                grown.Y > grown.X + 15f && grown.Y > grown.Z + 15f,
                $"With grass the floor's centre was {Show(grown)}, not clearly greener than red and blue; bare it is {Show(bare)}.");
        });
    }

    [Fact]
    public void Grass_switched_off_or_at_no_density_leaves_the_floor_bare()
    {
        WithFloor(floor =>
        {
            var bare = Centre(Render(View([]), floor));
            var switchedOff = Centre(Render(View([new GrassDraw(floor, new GrassSettings())], RenderEffects.Grass), floor));
            var none = Centre(Render(View([new GrassDraw(floor, new GrassSettings { Density = 0f })]), floor));
            output.WriteLine($"Centre: bare {Show(bare)}, grass off {Show(switchedOff)}, density 0 {Show(none)}.");

            Assert.True(
                Vector3.Distance(switchedOff, bare) <= SameTolerance,
                $"With grass switched off the floor's centre was {Show(switchedOff)}, but bare it is {Show(bare)}.");
            Assert.True(
                Vector3.Distance(none, bare) <= SameTolerance,
                $"With grass at density 0 the floor's centre was {Show(none)}, but bare it is {Show(bare)}.");
        });
    }

    // Runs the body with a level grey floor 40 metres across under the camera, then destroys it and checks the
    // validation errors did not grow.
    private void WithFloor(Action<DrawItem> body)
    {
        int errors = gpu.ValidationErrors;
        var quad = TestMeshes.Quad();
        var material = gpu.Renderer.Assets.CreateMaterial(new Material { BaseColor = new Vector4(0.5f, 0.5f, 0.5f, 1f), Roughness = 0.9f });
        var floor = gpu.Renderer.Assets.CreateDrawItem(quad.Vertices, quad.Indices, material)
            with { Transform = Matrix4x4.CreateScale(40f) * Matrix4x4.CreateRotationX(-MathF.PI / 2f) };
        try
        {
            body(floor);
        }
        finally
        {
            gpu.Renderer.Assets.DestroyMesh(floor.Mesh);
            gpu.Renderer.Assets.DestroyMaterial(material);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // The fixture's view moved up and looking down at the floor ahead, with the effects that add frame-to-frame noise or
    // change the exposure left off.
    private FrameView View(GrassDraw[] grass, RenderEffects off = RenderEffects.None)
    {
        var view = gpu.View(DebugView.None);
        view.View = Matrix4x4.CreateLookAt(Eye, new Vector3(0f, 0f, -1f), Vector3.UnitY);
        view.CameraPosition = Eye;
        view.Off = off | RenderEffects.AntiAliasing | RenderEffects.Reflections | RenderEffects.LightProbes
            | RenderEffects.SunShadows | RenderEffects.AutoExposure | RenderEffects.Bloom;
        view.Lights = Sun;
        view.Extras.Grass = grass;
        return view;
    }

    private byte[] Render(FrameView view, DrawItem floor)
    {
        DrawItem[] draws = [floor];
        for (int frame = 0; frame < Frames; frame++)
        {
            gpu.Renderer.Render(view, draws);
        }

        return gpu.Renderer.Capture(view, draws, out _, out _);
    }

    private static Vector3 Centre(byte[] pixels) => Mean(pixels, Size, Size / 2, Size / 2, SampleRadius);
}
