using System.Numerics;

namespace Rendition.Gpu.Tests;

/// <summary>
/// A bake whose draws don't all fit the lightmap pages is refused, rather than leaving the rest without baked light.
/// </summary>
[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class BakeTooManyDrawsGpuTests(GpuFixture gpu)
{
    private const int Density = 20;

    // A quad 51.2 metres a side at 20 texels a metre is a rectangle 1024 texels wide, and with its gutter only one
    // fits on a 2048 page.
    private const float BigSide = 1024f / Density;

    [Fact]
    public void A_space_with_more_draws_than_its_pages_hold_is_refused()
    {
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            // Eight pages hold eight of them, so the ninth does not fit.
            var draws = BigDraws(quad, 9);
            var space = new BakedSpace("big-room", new Vector3(-6f, -3f, -6f), new Vector3(6f, 7f, 6f), 1f);

            var error = Assert.Throws<InvalidOperationException>(() => BakeSpace(space, draws));

            Assert.Equal(
                "1 of the 9 static draws in the baked space \"big-room\" don't fit in 8 lightmap pages of 2048 texels, so they would get no baked light. Use fewer than 20 texels a metre, or bake a smaller space.",
                error.Message);
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_chunk_with_more_draws_than_its_page_holds_is_refused()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var quad = CreateQuad();
        try
        {
            // One page holds one of them, so the second does not fit.
            var draws = BigDraws(quad, 2);
            var chunk = new BakedChunk("big-chunk", Matrix4x4.Identity, new Vector3(-2f, -1f, -2f), new Vector3(2f, 2f, 2f), Density);

            var error = Assert.Throws<InvalidOperationException>(() => BakeChunk(chunk, draws));

            Assert.Equal(
                "1 of the 2 static draws in the baked chunk \"big-chunk\" don't fit in one lightmap page of 2048 texels, so they would get no baked light. Use fewer than 20 texels a metre, or make the chunk smaller.",
                error.Message);
        }
        finally
        {
            gpu.Renderer.DestroyMesh(quad);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private byte[] BakeSpace(BakedSpace space, DrawItem[] draws)
    {
        var view = gpu.View(DebugView.None);
        view.BakedSpace = space;
        return gpu.Renderer.Bake(view, draws, new BakeSettings(Density, BakeQuality.Preview));
    }

    private byte[] BakeChunk(BakedChunk chunk, DrawItem[] draws)
    {
        var view = gpu.View(DebugView.None);
        view.BakedChunks = new[] { chunk };
        return gpu.Renderer.BakeChunk(view, draws, 0, new BakeSettings(Density, BakeQuality.Preview));
    }

    // Each draw is its own instance, so none shares a rectangle.
    private DrawItem[] BigDraws(MeshHandle quad, int count)
    {
        var draws = new DrawItem[count];
        for (int i = 0; i < count; i++)
        {
            draws[i] = new DrawItem
            {
                Mesh = quad,
                IndexCount = 6,
                Material = gpu.Matte,
                Transform = Matrix4x4.CreateScale(BigSide) * Matrix4x4.CreateRotationX(-MathF.PI / 2f),
                BakeId = BakeIds.Of((ulong)(i + 1) << 12, 0),
            };
        }

        return draws;
    }

    private MeshHandle CreateQuad()
    {
        var (vertices, indices) = TestMeshes.Quad();
        for (int i = 0; i < vertices.Length; i++)
        {
            vertices[i].Uv2 = vertices[i].Uv;
        }

        var handle = gpu.Renderer.CreateMesh(vertices, indices);
        gpu.RenderOneFrame(); // Uploads the mesh.
        return handle;
    }
}
