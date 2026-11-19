using System.Numerics;

namespace Rendition.Gpu.Tests;

// A two-metre quad faces the camera, lit by the environment alone, which is the light occlusion takes away. Its
// occlusion texture is two texels, black on the left and white on the right. Its second UV set is its first turned
// left to right, so read through the first set the quad's left is hidden, and read through the second its right is.
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class OcclusionMapGpuTests(GpuFixture gpu)
{
    // The quad covers columns 24 to 232. These are a tenth of the way in from each of its sides, clear of the blend
    // between the two texels in the middle.
    private const int Left = 45;
    private const int Right = 211;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void The_occlusion_texture_darkens_the_side_its_uv_set_puts_black_on(bool onUv2)
    {
        int errors = gpu.ValidationErrors;
        var occlusion = gpu.Renderer.Assets.CreateTexture(
            TextureData.FromPixels(2, 1, TextureKind.Linear, [0, 0, 0, 255, 255, 255, 255, 255]), TextureEdges.Clamp);
        var plain = gpu.Renderer.Assets.CreateMaterial(Matte());
        var occluded = gpu.Renderer.Assets.CreateMaterial(Matte() with
        {
            Textures = new MaterialTextures { Occlusion = occlusion },
            Flags = onUv2 ? MaterialFlags.OcclusionOnUv2 : MaterialFlags.None,
        });
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.Assets.CreateMesh([.. vertices.Select(v => v with { Uv2 = new Vector2(1f - v.Uv.X, v.Uv.Y) })], indices);
        try
        {
            byte[] without = Capture(Quad(mesh, indices, plain));
            byte[] with = Capture(Quad(mesh, indices, occluded));

            int hidden = onUv2 ? Right : Left;
            int open = onUv2 ? Left : Right;
            float unoccluded = Brightness(without, hidden);
            Assert.True(unoccluded > 20f, $"The quad reads {unoccluded} without occlusion, too dark to show it.");
            Assert.True(Brightness(with, hidden) < unoccluded * 0.25f,
                $"Where the texture is black the quad reads {Brightness(with, hidden)}, and {unoccluded} without occlusion.");
            Assert.True(MathF.Abs(Brightness(with, open) - Brightness(without, open)) < Brightness(without, open) * 0.03f,
                $"Where the texture is white the quad reads {Brightness(with, open)}, and {Brightness(without, open)} without occlusion.");
        }
        finally
        {
            gpu.Renderer.Assets.DestroyMesh(mesh);
            gpu.Renderer.Assets.DestroyMaterial(plain);
            gpu.Renderer.Assets.DestroyMaterial(occluded);
            gpu.Renderer.Assets.DestroyTexture(occlusion);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private static Material Matte() => new() { BaseColor = new Vector4(0.5f, 0.5f, 0.5f, 1f), Roughness = 0.8f };

    private static DrawItem Quad(MeshHandle mesh, uint[] indices, MaterialHandle material) => new()
    {
        Mesh = mesh,
        IndexCount = (uint)indices.Length,
        Material = material,
        Transform = Matrix4x4.CreateScale(2f),
    };

    private byte[] Capture(DrawItem quad)
    {
        var view = gpu.View(DebugView.None);
        // No anti-aliasing, so one frame is the whole picture; no light probes or screen-space occlusion, which shade
        // the quad themselves.
        view.Off = RenderEffects.AntiAliasing | RenderEffects.LightProbes | RenderEffects.AmbientOcclusion;
        view.Environment.Intensity = 1f;
        return gpu.Renderer.Capture(view, [quad], out _, out _);
    }

    // The mean of a column over the middle of the quad, away from its top and bottom.
    private static float Brightness(byte[] pixels, int column)
    {
        float sum = 0f;
        for (int line = 100; line < 156; line++)
        {
            int at = (line * 256 + column) * 4;
            sum += (pixels[at] + pixels[at + 1] + pixels[at + 2]) / 3f;
        }

        return sum / 56f;
    }
}
