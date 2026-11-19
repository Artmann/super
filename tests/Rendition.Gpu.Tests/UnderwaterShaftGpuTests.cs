using System.Numerics;
using static Rendition.Gpu.Tests.TestPixels;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// Under water the shafts of light are drawn in the volumetric fog's grid, so the sun's shadow cuts them: a slab high
// over the left half of the view leaves the water there without shafts, while they shine beside it.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class UnderwaterShaftGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    private const int Size = 256;

    // Half the width of each square averaged, one in the left quarter of the picture and one in the right.
    private const int SampleRadius = 24;

    // The fog keeps 90% of last frame's cells, so it takes this many frames to settle.
    private const int SettleFrames = 40;

    // A slab 40 m across and half a metre thick, 4 m up, over everything left of x = 0.
    private static readonly Matrix4x4 SlabAt =
        Matrix4x4.CreateScale(40f, 0.5f, 40f) * Matrix4x4.CreateTranslation(-20f, 4f, -15f);

    [Fact]
    public void Underwater_shafts_are_cut_by_shadows()
    {
        int errors = gpu.ValidationErrors;
        var cube = TestMeshes.Cube();
        var material = gpu.Renderer.Assets.CreateMaterial(new Material { BaseColor = new Vector4(0.5f, 0.5f, 0.5f, 1f), Roughness = 1f });
        var slab = gpu.Renderer.Assets.CreateDrawItem(cube.Vertices, cube.Indices, material) with { Transform = SlabAt };
        try
        {
            var plain = Render(View(shafts: 0f), slab);
            var shafted = Render(View(shafts: 1f), slab);
            float shaded = Brightness(Left(shafted)) - Brightness(Left(plain));
            float lit = Brightness(Right(shafted)) - Brightness(Right(plain));
            output.WriteLine($"Left, under the slab: {Show(Left(plain))} without shafts, {Show(Left(shafted))} with.");
            output.WriteLine($"Right, in the sun: {Show(Right(plain))} without shafts, {Show(Right(shafted))} with.");

            Assert.True(lit > 20f, $"The shafts added only {lit:0.0} in the sun, so the check proves nothing.");
            Assert.True(
                shaded < lit * 0.2f,
                $"In the slab's shadow the shafts added {shaded:0.0}, against {lit:0.0} beside it.");
        }
        finally
        {
            gpu.Renderer.Assets.DestroyMesh(slab.Mesh);
            gpu.Renderer.Assets.DestroyMaterial(material);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // The fixture's view 10 m under clear, faintly blue water, with the sun straight overhead casting shadows over the
    // first 20 metres, and the effects that add frame-to-frame noise or change the exposure left off.
    private FrameView View(float shafts)
    {
        var view = gpu.View(DebugView.None);
        view.Off = RenderEffects.AntiAliasing | RenderEffects.Reflections | RenderEffects.LightProbes
            | RenderEffects.AutoExposure | RenderEffects.Bloom;
        view.Lights = new DirectionalLight[] { new() { Direction = Vector3.UnitY, Radiance = new Vector3(3f), CastsShadow = true } };
        view.Shadows = new ShadowSettings { Center = new Vector3(0f, 0f, -8f), Radius = 20f, Cascades = 1 };
        view.Weather.Water = new UnderwaterSettings
        {
            Absorption = new Vector3(0.03f, 0.02f, 0.02f),
            ShallowColor = new Vector3(0.3f, 0.6f, 0.8f),
            DeepColor = new Vector3(0.02f, 0.05f, 0.08f),
            SurfaceHeight = 10f,
            CausticStrength = 0f,
            CausticScale = 0.5f,
            ShaftStrength = shafts,
            ShaftDistance = 15f,
        };
        return view;
    }

    private byte[] Render(FrameView view, DrawItem slab)
    {
        DrawItem[] draws = [slab];
        for (int frame = 0; frame < SettleFrames; frame++)
        {
            gpu.Renderer.Render(view, draws);
        }

        return gpu.Renderer.Capture(view, draws, out _, out _);
    }

    // Below the middle of the picture, where the view looks out level and down through the water and never meets
    // the slab.
    private static Vector3 Left(byte[] pixels) => Mean(pixels, Size, Size / 4, Size * 5 / 8, SampleRadius);

    private static Vector3 Right(byte[] pixels) => Mean(pixels, Size, Size * 3 / 4, Size * 5 / 8, SampleRadius);
}
