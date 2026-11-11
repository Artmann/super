using System.Numerics;
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
    public void Building_before_the_mesh_is_uploaded_throws_and_caches_nothing()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var tracing = gpu.Renderer.Tracing!;
        int before = tracing.BottomLevelCount;
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);
        TracePart[] parts = [new(0, 6, gpu.Matte)];

        var error = Assert.Throws<InvalidOperationException>(
            () => gpu.Context.SubmitAndWait(cmd => tracing.BottomLevel(cmd, mesh, parts)));

        Assert.Equal(
            $"Mesh {mesh.Index}'s bytes haven't been uploaded yet; record the frame's uploads before building its structure.",
            error.Message);
        Assert.Equal(before, tracing.BottomLevelCount);

        // Once a frame has carried the upload, the same call builds.
        gpu.RenderOneFrame();
        ulong address = 0;
        gpu.Context.SubmitAndWait(cmd => address = tracing.BottomLevel(cmd, mesh, parts));
        Assert.NotEqual(0ul, address);
        gpu.Renderer.DestroyMesh(mesh);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Making_a_material_see_through_rebuilds_the_structures_that_use_it()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var tracing = gpu.Renderer.Tracing!;
        int before = tracing.BottomLevelCount;
        var opaque = new Material { BaseColor = new Vector4(1f), Roughness = 0.5f };
        var material = gpu.Renderer.CreateMaterial(opaque);
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);
        gpu.RenderOneFrame();
        TracePart[] parts = [new(0, 6, material)];
        TracePart[] matte = [new(0, 6, gpu.Matte)];

        ulong first = 0, untouched = 0;
        gpu.Context.SubmitAndWait(cmd =>
        {
            first = tracing.BottomLevel(cmd, mesh, parts);
            untouched = tracing.BottomLevel(cmd, mesh, matte);
        });

        // A change that keeps it opaque leaves the structure alone.
        gpu.Renderer.UpdateMaterial(material, opaque with { Roughness = 0.9f });
        Assert.Equal(before + 2, tracing.BottomLevelCount);

        gpu.Renderer.UpdateMaterial(material, opaque with { Flags = MaterialFlags.Transparent });
        Assert.Equal(before + 1, tracing.BottomLevelCount);

        ulong rebuilt = 0, matteAgain = 0;
        gpu.Context.SubmitAndWait(cmd =>
        {
            rebuilt = tracing.BottomLevel(cmd, mesh, parts);
            matteAgain = tracing.BottomLevel(cmd, mesh, matte);
        });

        Assert.NotEqual(first, rebuilt);
        Assert.Equal(untouched, matteAgain);
        Assert.Equal(before + 2, tracing.BottomLevelCount);
        gpu.Renderer.DestroyMesh(mesh);
        gpu.Renderer.DestroyMaterial(material);
        for (int i = 0; i < 3; i++)
        {
            gpu.RenderOneFrame();
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Destroying_a_material_forgets_the_structures_that_use_it()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var tracing = gpu.Renderer.Tracing!;
        int before = tracing.BottomLevelCount;
        var material = gpu.Renderer.CreateMaterial(new Material { BaseColor = new Vector4(1f), Roughness = 0.5f });
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);
        gpu.RenderOneFrame();
        gpu.Context.SubmitAndWait(cmd =>
        {
            tracing.BottomLevel(cmd, mesh, [new(0, 6, material)]);
            tracing.BottomLevel(cmd, mesh, [new(0, 6, gpu.Matte)]);
        });

        gpu.Renderer.DestroyMaterial(material);

        // Only the structure drawn in the destroyed material goes; its slot may come back as a see-through one.
        Assert.Equal(before + 1, tracing.BottomLevelCount);
        gpu.Renderer.DestroyMesh(mesh);
        for (int i = 0; i < 3; i++)
        {
            gpu.RenderOneFrame();
        }

        Assert.Equal(before, tracing.BottomLevelCount);
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

public sealed class AlignUpTests
{
    [Theory]
    [InlineData(1000ul, 128ul, 1024ul)]
    [InlineData(1024ul, 128ul, 1024ul)]
    [InlineData(1ul, 256ul, 256ul)]
    [InlineData(0ul, 64ul, 0ul)]
    public void Rounds_up_to_the_next_multiple(ulong value, ulong alignment, ulong expected)
    {
        Assert.Equal(expected, RayTracing.AlignUp(value, alignment));
    }
}
