using System.Numerics;

namespace Rendition.Gpu.Tests;

// Drawing depth ahead of shading is only a faster way to the same picture: what shows, what is hidden and what a
// cut-out lets through must not change with it.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class DepthFirstGpuTests(GpuFixture gpu)
{
    // Everything that changes from frame to frame is off, so two frames of one scene are the same picture, and the
    // light comes from the environment alone.
    private const RenderEffects Steady =
        RenderEffects.All & ~(RenderEffects.Materials | RenderEffects.EnvironmentLight | RenderEffects.DepthFirst);

    [Fact]
    public void A_wall_behind_a_panel_and_a_cut_out_draws_the_same_picture_depth_first_or_not()
    {
        int errors = gpu.ValidationErrors;
        var (vertices, indices) = TestMeshes.Quad();
        var red = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.8f, 0.1f, 0.1f, 1f), Roughness = 0.8f });
        var green = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(0.1f, 0.8f, 0.1f, 1f), Roughness = 0.8f });
        var cutAway = gpu.Renderer.CreateMaterial(new Material
        {
            BaseColor = new Vector4(0.1f, 0.1f, 0.8f, 0f), Roughness = 0.8f, AlphaCutoff = 0.5f,
        });
        var quad = gpu.Renderer.CreateDrawItem(vertices, indices, red);
        try
        {
            DrawItem[] scene =
            [
                // A red wall that fills the picture, drawn before the green panel that hides its middle.
                quad with { Transform = Matrix4x4.CreateScale(8f) * Matrix4x4.CreateTranslation(0f, 0f, -1f) },
                quad with { Material = green },
                // In front of both, a panel whose every pixel is cut away, which must hide nothing.
                quad with { Material = cutAway, Transform = Matrix4x4.CreateScale(4f) * Matrix4x4.CreateTranslation(0f, 0f, 1f) },
            ];

            byte[] plain = Capture(scene, Steady | RenderEffects.DepthFirst);
            byte[] depthFirst = Capture(scene, Steady);

            // The scene is what it was meant to be: green in the middle, red beside it.
            var middle = Pixel(plain, 0.5f, 0.5f);
            var side = Pixel(plain, 0.1f, 0.5f);
            Assert.True(middle.G > middle.R + 40, $"The middle reads {middle}, which isn't the green panel.");
            Assert.True(side.R > side.G + 40, $"The side reads {side}, which isn't the red wall.");

            // Two frames of one scene differ by a step or two of 255 in the output's dither. A surface gone missing, or
            // one showing through another, differs by the colours' whole distance.
            int furthest = 0;
            for (int i = 0; i < plain.Length; i++)
            {
                furthest = Math.Max(furthest, Math.Abs(plain[i] - depthFirst[i]));
            }

            Assert.Equal(plain.Length, depthFirst.Length);
            Assert.True(furthest <= 3, $"Some pixel differs by {furthest} of 255 with depth drawn first.");
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad.Mesh);
            gpu.Renderer.DestroyMaterial(red);
            gpu.Renderer.DestroyMaterial(green);
            gpu.Renderer.DestroyMaterial(cutAway);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private byte[] Capture(DrawItem[] draws, RenderEffects off)
    {
        var view = gpu.View(DebugView.None);
        view.Off = off;
        view.EnvironmentIntensity = 1f;
        return gpu.Renderer.Capture(view, draws, out _, out _);
    }

    private (byte R, byte G, byte B) Pixel(byte[] pixels, float across, float down)
    {
        var view = gpu.View(DebugView.None);
        int at = ((int)(down * view.Height) * view.Width + (int)(across * view.Width)) * 4;
        return (pixels[at], pixels[at + 1], pixels[at + 2]);
    }
}
