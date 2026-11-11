using System.Numerics;

namespace Rendition.Gpu.Tests;

[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class BakeIdGpuTests(GpuFixture gpu)
{
    [Fact]
    public void Static_draw_with_bones_is_explained()
    {
        int errors = gpu.ValidationErrors;
        var draw = Quad() with { BakeId = BakeIds.Of(1, 0), Bones = new BoneRange(0, 2) };

        var error = Assert.Throws<ArgumentException>(() => gpu.RenderOneFrame([draw]));

        Assert.Contains("static", error.Message);
        Assert.Contains("bones", error.Message);
        AFrameStillRenders(errors, draw.Mesh);
    }

    [Fact]
    public void Static_draw_that_swims_is_explained()
    {
        int errors = gpu.ValidationErrors;
        var draw = Quad() with { BakeId = BakeIds.Of(1, 0), Swim = new SwimMotion(0.1f, 1f, 0f) };

        var error = Assert.Throws<ArgumentException>(() => gpu.RenderOneFrame([draw]));

        Assert.Contains("static", error.Message);
        Assert.Contains("swim", error.Message);
        AFrameStillRenders(errors, draw.Mesh);
    }

    [Fact]
    public void Static_draw_without_bones_or_swim_renders()
    {
        int errors = gpu.ValidationErrors;
        var draw = Quad() with { BakeId = BakeIds.Of(1, 0) };

        gpu.RenderOneFrame([draw]);

        gpu.Renderer.DestroyMesh(draw.Mesh);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private DrawItem Quad()
    {
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);
        return new DrawItem
        {
            Mesh = mesh,
            IndexCount = (uint)indices.Length,
            Material = gpu.Matte,
            Transform = Matrix4x4.Identity,
        };
    }

    // The check throws before the frame starts, so the renderer is left able to draw the next one.
    private void AFrameStillRenders(int errors, MeshHandle mesh)
    {
        gpu.RenderOneFrame();
        gpu.Renderer.DestroyMesh(mesh);
        Assert.Equal(errors, gpu.ValidationErrors);
    }
}
