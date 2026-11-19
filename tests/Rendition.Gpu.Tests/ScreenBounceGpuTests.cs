using System.Numerics;
using static Rendition.Gpu.Tests.TestPixels;
using Xunit.Abstractions;

namespace Rendition.Gpu.Tests;

// A red glowing panel stands out from a grey wall, turned to face the wall's right half and the camera at once, in a
// scene with no other light. The screen bounce reads the panel's glow from last frame's picture and lights the wall
// beside it red. Wall more than the bounce's radius away stays dark, and with the bounce off nothing lights the wall.
// The bounce reads last frame's picture, so this also checks that the picture is kept with anti-aliasing and
// reflections off.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class ScreenBounceGpuTests(GpuFixture gpu, ITestOutputHelper output)
{
    private const int Size = 256;
    private const int SampleRadius = 8;

    // The wall is 7 metres from the camera, where a metre is about 44 pixels. Beside the panel is the wall a few
    // centimetres right of where it stands; far is 2.4 metres right of it, past the bounce's 2 metre radius.
    private const int BesideX = 128;
    private const int FarX = 220;

    // The bounce needs last frame's picture, so a few frames are drawn first.
    private const int SettleFrames = 8;

    [Fact]
    public void A_glowing_panel_lights_the_wall_beside_it_in_its_own_colour()
    {
        WithScene((wall, panel) =>
        {
            var on = Render(RenderEffects.None, wall, panel);
            var off = Render(RenderEffects.ScreenBounce, wall, panel);
            output.WriteLine($"Beside: bounce on {Show(Beside(on))}, off {Show(Beside(off))}.");
            output.WriteLine($"Far: bounce on {Show(Far(on))}, off {Show(Far(off))}.");

            var lit = Beside(on) - Beside(off);
            Assert.True(lit.X > 12f, $"Beside the panel the wall was {Show(Beside(on))} with the bounce and {Show(Beside(off))} without.");
            Assert.True(lit.X > lit.Y * 3f, $"The bounce added {Show(lit)} beside the red panel, which is not red.");
            Assert.True(
                Brightness(Far(on)) - Brightness(Far(off)) < 2f,
                $"Past the bounce's radius the wall was {Show(Far(on))} with the bounce and {Show(Far(off))} without.");
        });
    }

    // Runs the body with the wall and the panel, then destroys them and checks the validation errors did not grow.
    private void WithScene(Action<DrawItem, DrawItem> body)
    {
        int errors = gpu.ValidationErrors;
        var quad = TestMeshes.Quad();
        var grey = gpu.Renderer.Assets.CreateMaterial(new Material { BaseColor = new Vector4(0.7f, 0.7f, 0.7f, 1f), Roughness = 1f });
        var glow = gpu.Renderer.Assets.CreateMaterial(new Material
        {
            BaseColor = new Vector4(0f, 0f, 0f, 1f),
            Roughness = 1f,
            Emissive = new Vector3(6f, 0.2f, 0.2f),
        });
        var wall = gpu.Renderer.Assets.CreateDrawItem(quad.Vertices, quad.Indices, grey)
            with { Transform = Matrix4x4.CreateScale(20f) * Matrix4x4.CreateTranslation(0f, 0f, -4f) };
        // Turned 60 degrees, so it faces mostly right, along the wall, and partly the camera, which sees its glow.
        var panel = gpu.Renderer.Assets.CreateDrawItem(quad.Vertices, quad.Indices, glow)
            with { Transform = Matrix4x4.CreateScale(1.2f) * Matrix4x4.CreateRotationY(MathF.PI / 3f) * Matrix4x4.CreateTranslation(-0.6f, 0f, -3.45f) };
        try
        {
            body(wall, panel);
        }
        finally
        {
            gpu.Renderer.Assets.DestroyMesh(wall.Mesh);
            gpu.Renderer.Assets.DestroyMesh(panel.Mesh);
            gpu.Renderer.Assets.DestroyMaterial(grey);
            gpu.Renderer.Assets.DestroyMaterial(glow);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // No sun, sky or probes, so only the bounce can light the wall, and none of the effects that change the exposure
    // or spread the glow.
    private byte[] Render(RenderEffects off, DrawItem wall, DrawItem panel)
    {
        var view = gpu.View(DebugView.None);
        view.Off = off | RenderEffects.AntiAliasing | RenderEffects.Reflections | RenderEffects.LightProbes
            | RenderEffects.AutoExposure | RenderEffects.Bloom;
        view.Lights = [];
        view.Environment.Intensity = 0f;
        DrawItem[] draws = [wall, panel];
        for (int frame = 0; frame < SettleFrames; frame++)
        {
            gpu.Renderer.Render(view, draws);
        }

        return gpu.Renderer.Capture(view, draws, out _, out _);
    }

    private static Vector3 Beside(byte[] pixels) => Mean(pixels, Size, BesideX, Size / 2, SampleRadius);

    private static Vector3 Far(byte[] pixels) => Mean(pixels, Size, FarX, Size / 2, SampleRadius);
}
