using System.Numerics;
using Rendition;

/// <summary>A unit cube with one flat-shaded quad per face, wound counter-clockwise from outside.</summary>
internal static class Cube
{
    public static readonly Vertex[] Vertices = CreateVertices();
    public static readonly uint[] Indices = CreateIndices();

    private static Vertex[] CreateVertices()
    {
        ReadOnlySpan<(Vector3 Normal, Vector3 U, Vector3 V)> faces =
        [
            (Vector3.UnitX, -Vector3.UnitZ, Vector3.UnitY),
            (-Vector3.UnitX, Vector3.UnitZ, Vector3.UnitY),
            (Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ),
            (-Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
            (Vector3.UnitZ, Vector3.UnitX, Vector3.UnitY),
            (-Vector3.UnitZ, -Vector3.UnitX, Vector3.UnitY),
        ];

        var vertices = new Vertex[faces.Length * 4];
        for (int i = 0; i < faces.Length; i++)
        {
            var (normal, u, v) = faces[i];
            vertices[i * 4 + 0] = new Vertex((normal - u - v) * 0.5f, normal);
            vertices[i * 4 + 1] = new Vertex((normal + u - v) * 0.5f, normal);
            vertices[i * 4 + 2] = new Vertex((normal + u + v) * 0.5f, normal);
            vertices[i * 4 + 3] = new Vertex((normal - u + v) * 0.5f, normal);
        }

        return vertices;
    }

    private static uint[] CreateIndices()
    {
        ReadOnlySpan<uint> quad = [0, 1, 2, 0, 2, 3];
        var indices = new uint[6 * quad.Length];
        for (int i = 0; i < indices.Length; i++)
        {
            int face = i / quad.Length;
            indices[i] = (uint)face * 4 + quad[i % quad.Length];
        }

        return indices;
    }
}
