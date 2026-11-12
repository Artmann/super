using System.Numerics;
using Rendition.Vulkan;

namespace Rendition.Gpu.Tests;

[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class TopLevelTests(GpuFixture gpu)
{
    [Fact]
    public void Builds_an_empty_scene()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var tracing = gpu.Renderer.Tracing!;
        int before = tracing.BottomLevelCount;

        TopLevelStructure built = default;
        gpu.Context.SubmitAndWait(cmd => built = tracing.TopLevel(cmd, []));

        Assert.NotEqual(0ul, built.Address);
        Assert.Equal(0, built.GeometryCount);
        Assert.Equal(before, tracing.BottomLevelCount);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Two_placements_of_one_mesh_share_one_bottom_level_structure()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var tracing = gpu.Renderer.Tracing!;
        int before = tracing.BottomLevelCount;
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);
        gpu.RenderOneFrame();
        TracePart[] parts = [new(0, 6, gpu.Matte)];
        TraceInstance[] instances =
        [
            new(Matrix4x4.Identity, mesh, parts, TraceFlags.None),
            new(Matrix4x4.CreateTranslation(2, 0, 0), mesh, parts, TraceFlags.None),
        ];

        TopLevelStructure built = default;
        gpu.Context.SubmitAndWait(cmd => built = tracing.TopLevel(cmd, instances));

        Assert.NotEqual(0ul, built.Address);
        // Each placement gets its own entry in the table the shaders read, pointing at the mesh's own buffers.
        var gpuMesh = gpu.Renderer.MeshFor(mesh);
        var entries = built.ReadGeometries();
        Assert.Equal(2, entries.Length);
        foreach (var entry in entries)
        {
            Assert.Equal(gpuMesh.Vertices.Address, entry.Vertices);
            Assert.Equal(gpuMesh.Indices.Address, entry.Indices);
            Assert.Equal(0u, entry.FirstIndex);
            Assert.Equal((uint)gpu.Matte.Index, entry.Material);
        }

        Assert.Equal(before + 1, tracing.BottomLevelCount);
        gpu.Renderer.DestroyMesh(mesh);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Builds_recorded_together_get_their_own_structures_and_reuse_them_once_done()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var tracing = gpu.Renderer.Tracing!;

        // Neither build has run when the second is recorded, so they mustn't share a structure or its instances.
        TopLevelStructure first = default, second = default;
        gpu.Context.SubmitAndWait(cmd =>
        {
            first = tracing.TopLevel(cmd, []);
            second = tracing.TopLevel(cmd, []);
        });
        Assert.NotEqual(first.Address, second.Address);

        // Once the frames that might read them are done, they're built into again rather than made anew.
        for (int i = 0; i < 3; i++)
        {
            gpu.RenderOneFrame();
        }

        int made = tracing.TopLevelStructuresMade;
        gpu.Context.SubmitAndWait(cmd => tracing.TopLevel(cmd, []));
        Assert.Equal(made, tracing.TopLevelStructuresMade);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Builds_bigger_scenes_after_smaller_ones()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var tracing = gpu.Renderer.Tracing!;
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);
        gpu.RenderOneFrame();
        TracePart[] parts = [new(0, 6, gpu.Matte)];

        // Each size outgrows the last, so the buffers behind the structure have to grow between builds.
        foreach (int count in new[] { 1, 3, 40, 300 })
        {
            var instances = Enumerable.Range(0, count)
                .Select(i => new TraceInstance(Matrix4x4.CreateTranslation(i, 0, 0), mesh, parts, TraceFlags.None))
                .ToArray();
            TopLevelStructure built = default;
            gpu.Context.SubmitAndWait(cmd => built = tracing.TopLevel(cmd, instances));
            Assert.Equal(count, built.GeometryCount);
        }

        gpu.Renderer.DestroyMesh(mesh);
        for (int i = 0; i < 3; i++)
        {
            gpu.RenderOneFrame();
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_frame_that_traces_builds_from_its_draws()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var tracing = gpu.Renderer.Tracing!;
        int before = tracing.BottomLevelCount;
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);
        DrawItem[] draws = [new() { Mesh = mesh, IndexCount = 6, Material = gpu.Matte, Transform = Matrix4x4.Identity }];

        // The mesh's bytes ride this same frame, so the build has to come after the frame's uploads.
        gpu.RenderOneFrame(draws, DebugView.Trace);

        Assert.Equal(before + 1, tracing.BottomLevelCount);
        gpu.Renderer.DestroyMesh(mesh);
        for (int i = 0; i < 3; i++)
        {
            gpu.RenderOneFrame();
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_frame_that_does_not_trace_builds_nothing()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var tracing = gpu.Renderer.Tracing!;
        int before = tracing.BottomLevelCount;
        int builds = tracing.TopLevelBuilds;
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);
        DrawItem[] draws = [new() { Mesh = mesh, IndexCount = 6, Material = gpu.Matte, Transform = Matrix4x4.Identity }];

        // Light probes trace their updates where the GPU can, so they are switched off to leave a frame with nothing to trace.
        gpu.RenderOneFrame(draws, off: RenderEffects.LightProbes);

        Assert.Equal(before, tracing.BottomLevelCount);
        Assert.Equal(builds, tracing.TopLevelBuilds);
        gpu.Renderer.DestroyMesh(mesh);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Destroying_a_traced_mesh_waits_for_the_frames_that_traced_it()
    {
        Assert.True(gpu.Renderer.CanTrace, "This test needs a GPU that can trace rays.");
        int errors = gpu.ValidationErrors;
        var tracing = gpu.Renderer.Tracing!;
        var (vertices, indices) = TestMeshes.Quad();
        var mesh = gpu.Renderer.CreateMesh(vertices, indices);
        DrawItem[] draws = [new() { Mesh = mesh, IndexCount = 6, Material = gpu.Matte, Transform = Matrix4x4.Identity }];
        gpu.RenderOneFrame();

        gpu.RenderOneFrame(draws, DebugView.Trace);

        // Render doesn't wait for the GPU, so the frame that built the top level over this mesh may still be running.
        int released = tracing.BottomLevelsReleased;
        gpu.Renderer.DestroyMesh(mesh);
        Assert.Equal(released, tracing.BottomLevelsReleased);

        for (int i = 0; i < 3; i++)
        {
            gpu.RenderOneFrame();
        }

        Assert.Equal(released + 1, tracing.BottomLevelsReleased);
        Assert.Equal(errors, gpu.ValidationErrors);
    }
}
