using System.Numerics;

namespace Rendition.Gpu.Tests;

[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class BakeAreaGpuTests(GpuFixture gpu)
{
    [Fact]
    public void Bounds_of_a_uploaded_cube_hold_its_corners()
    {
        int errors = gpu.ValidationErrors;
        var vertices = new Vertex[8];
        for (int i = 0; i < vertices.Length; i++)
        {
            var corner = new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f);
            vertices[i] = new Vertex(corner, Vector3.Normalize(corner), Vector4.UnitX, Vector2.Zero);
        }

        uint[] indices =
        [
            0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6,
            0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7,
            0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5,
        ];
        var cube = gpu.Renderer.CreateDrawItem(vertices, indices, gpu.Matte);

        try
        {
            var bounds = gpu.Renderer.BoundsOf(cube.Detail);

            Assert.True(bounds.Center.Length() <= 1e-4f, $"The centre is {bounds.Center}, not the origin.");
            Assert.True(bounds.Radius >= MathF.Sqrt(3f) / 2f, $"The radius {bounds.Radius} misses the corners.");
            foreach (var vertex in vertices)
            {
                Assert.True(Vector3.Distance(vertex.Position, bounds.Center) <= bounds.Radius + 1e-5f);
            }
        }
        finally
        {
            gpu.Renderer.DestroyMesh(cube.Mesh);
        }

        gpu.RenderOneFrame();
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Bounds_of_a_part_without_detail_are_the_point_at_its_origin()
    {
        Assert.Equal(new BakeSphere(Vector3.Zero, 0f), gpu.Renderer.BoundsOf(default));
    }
}
