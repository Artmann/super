using System.Numerics;

namespace Rendition.Gpu.Tests;

// A grey cube, scaled and placed by a transform, for a test that needs something in front of the sky.
internal sealed class TestWall : IDisposable
{
    private readonly GpuFixture _gpu;
    private readonly MeshHandle _cube;

    public TestWall(GpuFixture gpu, Matrix4x4 transform)
    {
        _gpu = gpu;
        var (vertices, indices) = TestMeshes.Cube();
        _cube = gpu.Renderer.Assets.CreateMesh(vertices, indices);
        gpu.RenderOneFrame(); // Uploads the mesh.
        Draws =
        [
            new DrawItem
            {
                Mesh = _cube,
                IndexCount = (uint)indices.Length,
                Material = gpu.Matte,
                Transform = transform,
            },
        ];
    }

    public DrawItem[] Draws { get; }

    public void Dispose() => _gpu.Renderer.Assets.DestroyMesh(_cube);
}
