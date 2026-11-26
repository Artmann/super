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

    // A wall from x = 0 to 8, five metres away, covering the right half of a picture looking down -Z.
    public static TestWall RightHalf(GpuFixture gpu) =>
        new(gpu, Matrix4x4.CreateScale(8f, 40f, 0.1f) * Matrix4x4.CreateTranslation(4f, 0f, -5f));

    public void Dispose() => _gpu.Renderer.Assets.DestroyMesh(_cube);
}
