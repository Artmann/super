using System.Numerics;

namespace Rendition.Gpu.Tests;

internal static class TestMeshes
{
    /// <summary>A unit square in the XY plane, facing +Z, as two triangles.</summary>
    public static (Vertex[] Vertices, uint[] Indices) Quad()
    {
        var tangent = new Vector4(1, 0, 0, 1);
        Vertex[] vertices =
        [
            new(new Vector3(-0.5f, -0.5f, 0), Vector3.UnitZ, tangent, new Vector2(0, 0)),
            new(new Vector3(0.5f, -0.5f, 0), Vector3.UnitZ, tangent, new Vector2(1, 0)),
            new(new Vector3(0.5f, 0.5f, 0), Vector3.UnitZ, tangent, new Vector2(1, 1)),
            new(new Vector3(-0.5f, 0.5f, 0), Vector3.UnitZ, tangent, new Vector2(0, 1)),
        ];

        return (vertices, [0, 1, 2, 0, 2, 3]);
    }
}
