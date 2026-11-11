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

    /// <summary>A unit cube round the origin, four vertices a face so each face has its own normal.</summary>
    public static (Vertex[] Vertices, uint[] Indices) Cube()
    {
        Vector3[] normals = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        foreach (var normal in normals)
        {
            // Two axes across the face, chosen so (across, up, normal) turns the same way as (x, y, z).
            var up = MathF.Abs(normal.Y) > 0.5f ? Vector3.UnitZ : Vector3.UnitY;
            var across = Vector3.Cross(up, normal);
            uint first = (uint)vertices.Count;
            var tangent = new Vector4(across, 1f);
            vertices.Add(new Vertex((normal - across - up) * 0.5f, normal, tangent, new Vector2(0, 0)));
            vertices.Add(new Vertex((normal + across - up) * 0.5f, normal, tangent, new Vector2(1, 0)));
            vertices.Add(new Vertex((normal + across + up) * 0.5f, normal, tangent, new Vector2(1, 1)));
            vertices.Add(new Vertex((normal - across + up) * 0.5f, normal, tangent, new Vector2(0, 1)));
            indices.AddRange([first, first + 1, first + 2, first, first + 2, first + 3]);
        }

        return ([.. vertices], [.. indices]);
    }
}
