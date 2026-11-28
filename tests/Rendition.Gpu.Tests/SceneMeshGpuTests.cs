using System.Numerics;

namespace Rendition.Gpu.Tests;

// The scene draws its meshes through the GPU layer, which knows each mesh's buffers by the ids it adopted them under.
[Collection(GpuCollection.Name)]
[Trait("Needs", "GPU")]
public sealed class SceneMeshGpuTests(GpuFixture gpu)
{
    [Fact]
    public void A_destroyed_mesh_lets_go_of_its_buffers_and_the_next_mesh_draws_without_validation_errors()
    {
        int errors = gpu.ValidationErrors;
        var stores = gpu.Renderer.FrameParts.SceneStores;

        var (cubeVertices, cubeIndices) = TestMeshes.Cube();
        var cube = gpu.Renderer.Assets.CreateMesh(cubeVertices, cubeIndices);
        var cubeMesh = stores.MeshFor(cube);
        int[] cubeIds = [cubeMesh.VertexBuffer.Value, cubeMesh.IndexBuffer.Value];
        gpu.RenderOneFrame(); // Uploads the mesh.
        gpu.RenderOneFrame([Draw(cube, cubeIndices.Length)]);
        gpu.Renderer.Assets.DestroyMesh(cube);

        // A destroyed mesh is let go of two frames later, once no frame in flight draws it. The layer gives freed ids
        // out again, so the next mesh is known by the cube's ids only if the cube let go of them.
        gpu.RenderOneFrame();
        gpu.RenderOneFrame();
        gpu.RenderOneFrame();
        var (quadVertices, quadIndices) = TestMeshes.Quad();
        var quad = gpu.Renderer.Assets.CreateMesh(quadVertices, quadIndices);
        var quadMesh = stores.MeshFor(quad);
        int[] quadIds = [quadMesh.VertexBuffer.Value, quadMesh.IndexBuffer.Value];
        Assert.Equal(cubeIds.Order(), quadIds.Order());

        gpu.RenderOneFrame();
        gpu.RenderOneFrame([Draw(quad, quadIndices.Length)]);
        gpu.Renderer.Assets.DestroyMesh(quad);

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private DrawItem Draw(MeshHandle mesh, int indexCount) => new()
    {
        Mesh = mesh,
        IndexCount = (uint)indexCount,
        Material = gpu.Matte,
        Transform = Matrix4x4.CreateTranslation(0f, 0f, -3f),
    };
}
