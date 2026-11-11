using System.Numerics;
using Rendition;

namespace Super.Tests;

/// <summary>Small meshes for the lightmap unwrapper's tests, by name.</summary>
internal static class TestShapes
{
    public static (Vertex[] Vertices, uint[] Indices) Get(string name) => name switch
    {
        "welded sphere" => Sphere(seamSplit: false),
        "seam-split sphere" => Sphere(seamSplit: true),
        "box" => Box(new Vector3(1f, 1f, 1f), mirrored: false),
        "box with one long side" => Box(new Vector3(4f, 1f, 1f), mirrored: false),
        "mirrored box" => Box(new Vector3(1f, 1f, 1f), mirrored: true),
        "helical ramp" => HelicalRamp(),
        "single triangle" => SingleTriangle(),
        "unshared triangles" => Unshared(Sphere(seamSplit: false, rings: 6, segments: 10)),
        "zero-area triangles" => WithZeroAreaTriangles(),
        "strip 40 by 0.2" => Strip(40f, 0.2f, 40),
        _ => throw new ArgumentException($"There is no test shape called \"{name}\".", nameof(name)),
    };

    /// <summary>
    /// A UV sphere of radius 1 with smooth normals. Seam-split, the column of vertices at longitude zero is
    /// duplicated at the far end of each ring, as a first-UV seam does, with exactly the same positions and normals.
    /// The triangles are the same, in the same order, either way.
    /// </summary>
    private static (Vertex[], uint[]) Sphere(bool seamSplit, int rings = 12, int segments = 18)
    {
        var vertices = new List<Vertex>();
        int columns = seamSplit ? segments + 1 : segments;

        vertices.Add(Smooth(Vector3.UnitY));
        for (int ring = 1; ring < rings; ring++)
        {
            float polar = MathF.PI * ring / rings;
            for (int column = 0; column < columns; column++)
            {
                if (column == segments)
                {
                    // Exactly the first vertex of the ring again, not cos(2π), so positions weld.
                    vertices.Add(vertices[1 + (ring - 1) * columns]);
                    continue;
                }

                float azimuth = 2f * MathF.PI * column / segments;
                var p = new Vector3(MathF.Sin(polar) * MathF.Cos(azimuth), MathF.Cos(polar), MathF.Sin(polar) * MathF.Sin(azimuth));
                vertices.Add(Smooth(p));
            }
        }

        vertices.Add(Smooth(-Vector3.UnitY));
        uint bottom = (uint)(vertices.Count - 1);

        uint At(int ring, int column)
        {
            // The last column of a seam-split ring is the duplicate; the welded sphere wraps to column zero.
            int c = seamSplit ? column : column % segments;
            return (uint)(1 + (ring - 1) * columns + c);
        }

        var indices = new List<uint>();
        for (int column = 0; column < segments; column++)
        {
            indices.AddRange([0u, At(1, column + 1), At(1, column)]);
        }

        for (int ring = 1; ring < rings - 1; ring++)
        {
            for (int column = 0; column < segments; column++)
            {
                uint a = At(ring, column);
                uint b = At(ring, column + 1);
                uint c = At(ring + 1, column + 1);
                uint d = At(ring + 1, column);
                indices.AddRange([a, b, c, a, c, d]);
            }
        }

        for (int column = 0; column < segments; column++)
        {
            indices.AddRange([bottom, At(rings - 1, column), At(rings - 1, column + 1)]);
        }

        return (vertices.ToArray(), indices.ToArray());
    }

    private static Vertex Smooth(Vector3 p) => new(p, p, new Vector4(1f, 0f, 0f, 1f), new Vector2(p.X, p.Z));

    /// <summary>
    /// A box of the given size with hard edges: 24 vertices, a flat normal per face. Mirrored, positions and normals
    /// are flipped in x as a scale of (-1, 1, 1) does, and the winding is left as it was.
    /// </summary>
    private static (Vertex[], uint[]) Box(Vector3 size, bool mirrored)
    {
        Vector3[] normals = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        var flip = mirrored ? new Vector3(-1f, 1f, 1f) : Vector3.One;
        var vertices = new List<Vertex>();
        var indices = new List<uint>();
        foreach (var n in normals)
        {
            var side = MathF.Abs(n.Y) > 0.5f ? Vector3.UnitX : Vector3.UnitY;
            var up = Vector3.Cross(n, side);
            uint start = (uint)vertices.Count;
            Vector2[] corners = [new(-1f, -1f), new(1f, -1f), new(1f, 1f), new(-1f, 1f)];
            foreach (var corner in corners)
            {
                var p = (n + side * corner.X + up * corner.Y) * 0.5f * size;
                vertices.Add(new Vertex(p * flip, n * flip, new Vector4(side, 1f), corner));
            }

            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }

        return (vertices.ToArray(), indices.ToArray());
    }

    /// <summary>
    /// A ramp 1.5 turns round a post, 1 m wide from radius 1 to 2, rising 1 m a turn, so every normal is within 10
    /// degrees of up and its top half turn lies right over its bottom one.
    /// </summary>
    private static (Vertex[], uint[]) HelicalRamp()
    {
        const int steps = 54;
        var vertices = new List<Vertex>();
        for (int step = 0; step <= steps; step++)
        {
            float turn = 1.5f * step / steps;
            float angle = turn * 2f * MathF.PI;
            var outward = new Vector3(MathF.Cos(angle), 0f, MathF.Sin(angle));
            foreach (float radius in new[] { 1f, 2f })
            {
                var p = outward * radius + new Vector3(0f, turn, 0f);

                // The surface rises 1 m along 2πr of travel; tilt up against the direction of travel by that slope.
                var along = new Vector3(-MathF.Sin(angle), 0f, MathF.Cos(angle));
                var normal = Vector3.Normalize(Vector3.UnitY - along / (2f * MathF.PI * radius));
                vertices.Add(new Vertex(p, normal, new Vector4(along, 1f), new Vector2(turn, radius)));
            }
        }

        var indices = new List<uint>();
        for (int step = 0; step < steps; step++)
        {
            uint inner = (uint)(step * 2);
            uint outer = inner + 1;
            uint nextInner = inner + 2;
            uint nextOuter = inner + 3;
            indices.AddRange([inner, nextInner, nextOuter, inner, nextOuter, outer]);
        }

        return (vertices.ToArray(), indices.ToArray());
    }

    private static (Vertex[], uint[]) SingleTriangle()
    {
        Vertex[] vertices =
        [
            new(new Vector3(0f, 0f, 0f), Vector3.UnitZ, new Vector4(1f, 0f, 0f, 1f), Vector2.Zero),
            new(new Vector3(1f, 0f, 0f), Vector3.UnitZ, new Vector4(1f, 0f, 0f, 1f), Vector2.UnitX),
            new(new Vector3(0f, 1f, 0f), Vector3.UnitZ, new Vector4(1f, 0f, 0f, 1f), Vector2.UnitY),
        ];
        return (vertices, [0u, 1u, 2u]);
    }

    /// <summary>The mesh as a triangle soup: three vertices of its own for every triangle, with a flat normal.</summary>
    private static (Vertex[], uint[]) Unshared((Vertex[] Vertices, uint[] Indices) mesh)
    {
        var vertices = new Vertex[mesh.Indices.Length];
        var indices = new uint[mesh.Indices.Length];
        for (int corner = 0; corner < mesh.Indices.Length; corner += 3)
        {
            var a = mesh.Vertices[mesh.Indices[corner]].Position;
            var b = mesh.Vertices[mesh.Indices[corner + 1]].Position;
            var c = mesh.Vertices[mesh.Indices[corner + 2]].Position;
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            for (int i = 0; i < 3; i++)
            {
                vertices[corner + i] = mesh.Vertices[mesh.Indices[corner + i]] with { Normal = normal };
                indices[corner + i] = (uint)(corner + i);
            }
        }

        return (vertices, indices);
    }

    /// <summary>
    /// A 1 m square of two triangles, plus a needle along its edge that sticks out past the square, a triangle with
    /// two corners at one place on its edge and one with all three at its corner.
    /// </summary>
    private static (Vertex[], uint[]) WithZeroAreaTriangles()
    {
        Vertex At(float x, float y) => new(new Vector3(x, y, 0f), Vector3.UnitZ, new Vector4(1f, 0f, 0f, 1f), new Vector2(x, y));
        Vertex[] vertices = [At(0f, 0f), At(1f, 0f), At(1f, 1f), At(0f, 1f), At(1.5f, 0f), At(0.5f, 0f), At(0.5f, 0f)];
        uint[] indices = [0, 1, 2, 0, 2, 3, 1, 0, 4, 0, 5, 6, 3, 3, 3];
        return (vertices, indices);
    }

    /// <summary>A flat upright strip, <paramref name="quads"/> quads of one row along x.</summary>
    private static (Vertex[], uint[]) Strip(float length, float height, int quads)
    {
        var vertices = new List<Vertex>();
        for (int i = 0; i <= quads; i++)
        {
            float x = length * i / quads;
            vertices.Add(new Vertex(new Vector3(x, 0f, 0f), Vector3.UnitZ, new Vector4(1f, 0f, 0f, 1f), new Vector2(x, 0f)));
            vertices.Add(new Vertex(new Vector3(x, height, 0f), Vector3.UnitZ, new Vector4(1f, 0f, 0f, 1f), new Vector2(x, height)));
        }

        var indices = new List<uint>();
        for (int i = 0; i < quads; i++)
        {
            uint a = (uint)(i * 2);
            indices.AddRange([a, a + 2, a + 3, a, a + 3, a + 1]);
        }

        return (vertices.ToArray(), indices.ToArray());
    }
}
