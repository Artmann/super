using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Tests;

[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class BottomLevelTests(GpuFixture gpu)
{
    [Fact]
    public void Builds_once_and_reuses_it()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var tracing = gpu.Renderer.Tracing!;
        int before = tracing.BottomLevelCount;
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);
        gpu.RenderOneFrame();
        TracePart[] parts = [new(0, 6, gpu.Matte)];

        ulong first = 0, second = 0;
        gpu.Context.SubmitAndWait(cmd => first = tracing.BottomLevel(cmd, mesh, parts));
        gpu.Context.SubmitAndWait(cmd => second = tracing.BottomLevel(cmd, mesh, parts));

        Assert.NotEqual(0ul, first);
        Assert.Equal(first, second);
        Assert.Equal(before + 1, tracing.BottomLevelCount);
        gpu.Renderer.DestroyMesh(mesh);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_different_part_list_gets_its_own_structure()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var tracing = gpu.Renderer.Tracing!;
        int before = tracing.BottomLevelCount;
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);
        gpu.RenderOneFrame();

        ulong whole = 0, half = 0, split = 0;
        gpu.Context.SubmitAndWait(cmd =>
        {
            whole = tracing.BottomLevel(cmd, mesh, [new(0, 6, gpu.Matte)]);
            half = tracing.BottomLevel(cmd, mesh, [new(0, 3, gpu.Matte)]);
            split = tracing.BottomLevel(cmd, mesh, [new(0, 3, gpu.Matte), new(3, 3, gpu.Matte)]);
        });

        Assert.Equal(3, new HashSet<ulong> { whole, half, split }.Count);
        Assert.Equal(before + 3, tracing.BottomLevelCount);
        gpu.Renderer.DestroyMesh(mesh);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Destroying_the_mesh_frees_its_structure()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var tracing = gpu.Renderer.Tracing!;
        int before = tracing.BottomLevelCount;
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);
        gpu.RenderOneFrame();
        gpu.Context.SubmitAndWait(cmd => tracing.BottomLevel(cmd, mesh, [new(0, 6, gpu.Matte)]));

        gpu.Renderer.DestroyMesh(mesh);
        Assert.Equal(before, tracing.BottomLevelCount);

        // The structure is released once the frames that might read it are done, like the mesh's buffers.
        for (int i = 0; i < 3; i++)
        {
            gpu.RenderOneFrame();
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_renderer_that_cannot_trace_has_no_tracing_and_still_draws()
    {
        using var fresh = new GpuFixture(disableRayTracing: true);
        var (vertices, indices) = TestMeshes.Quad();
        fresh.Renderer.CreateMesh(vertices, indices);
        fresh.RenderOneFrame();

        Assert.False(fresh.Renderer.CanTrace);
        Assert.Null(fresh.Renderer.Tracing);
        Assert.Equal(0, fresh.ValidationErrors);
    }
}

public sealed class GeometryFlagsTests
{
    [Theory]
    [InlineData(false, MaterialFlags.None, GeometryFlagsKHR.OpaqueBitKhr)]
    [InlineData(false, MaterialFlags.TwoSided, GeometryFlagsKHR.OpaqueBitKhr)]
    [InlineData(true, MaterialFlags.None, GeometryFlagsKHR.None)]
    [InlineData(false, MaterialFlags.Transparent, GeometryFlagsKHR.None)]
    public void Only_surfaces_without_holes_or_see_through_parts_are_opaque(bool cutouts, MaterialFlags flags, GeometryFlagsKHR expected)
    {
        Assert.Equal(expected, RayTracing.GeometryFlagsFor(cutouts, flags));
    }
}
