using System.Numerics;
using Rendition;
using Super;

namespace Reef;

/// <summary>A point on a branch's spine and how thick the branch is there.</summary>
internal readonly record struct Joint(Vector3 Position, float Radius);

/// <summary>Gathers the pieces of one coral or plant into a single mesh, so it costs one draw.</summary>
internal sealed class MeshParts(float uvPerMetre)
{
    private readonly List<Vertex> _vertices = [];
    private readonly List<uint> _indices = [];

    /// <summary>
    /// A round branch through <paramref name="spine"/>, thinning as its radii say, closed at the far end with a rounded
    /// tip. The near end is left open, since it starts inside the branch or ground it grows from.
    /// </summary>
    public void Branch(IReadOnlyList<Joint> spine, int sides)
    {
        if (spine.Count < 2)
        {
            throw new ArgumentException("A branch needs at least two joints.", nameof(spine));
        }

        uint first = (uint)_vertices.Count;
        var forward = Vector3.Normalize(spine[1].Position - spine[0].Position);
        var side = Vector3.Normalize(Vector3.Cross(forward, MathF.Abs(forward.Y) < 0.9f ? Vector3.UnitY : Vector3.UnitX));
        float along = 0f;
        for (int ring = 0; ring < spine.Count; ring++)
        {
            if (ring > 0)
            {
                along += Vector3.Distance(spine[ring].Position, spine[ring - 1].Position);
            }

            // Carry the sideways direction along the spine, turning it only as much as the spine turns, so the rings
            // don't twist.
            var next = ring + 1 < spine.Count ? spine[ring + 1].Position - spine[ring].Position : spine[ring].Position - spine[ring - 1].Position;
            var heading = ring == 0 ? forward : Vector3.Normalize(Vector3.Normalize(next) + forward);
            side = Vector3.Normalize(side - heading * Vector3.Dot(side, heading));
            forward = Vector3.Normalize(next);
            Ring(spine[ring], heading, side, sides, along);
        }

        // The tip: a point a little past the last joint, as far out as the branch is thick there.
        var last = spine[^1];
        var tip = last.Position + forward * last.Radius;
        _vertices.Add(new Vertex(tip, forward, new Vector4(side, 1f), new Vector2(0.5f, (along + last.Radius) * uvPerMetre)));
        StitchRings(first, spine.Count, sides);
    }

    /// <summary>
    /// Joins <paramref name="rings"/> rings of <paramref name="sides"/> sides, starting at vertex <paramref name="first"/>,
    /// into a tube, and closes the last ring onto the tip vertex that follows it.
    /// </summary>
    private void StitchRings(uint first, int rings, int sides)
    {
        for (int ring = 0; ring + 1 < rings; ring++)
        {
            uint row = first + (uint)(ring * (sides + 1));
            for (int i = 0; i < sides; i++)
            {
                uint a = row + (uint)i;
                uint b = a + (uint)(sides + 1);
                _indices.AddRange([a, a + 1, b, a + 1, b + 1, b]);
            }
        }

        uint lastRow = first + (uint)((rings - 1) * (sides + 1));
        uint tipIndex = lastRow + (uint)(sides + 1);
        for (int i = 0; i < sides; i++)
        {
            _indices.AddRange([lastRow + (uint)i, lastRow + (uint)i + 1, tipIndex]);
        }
    }

    /// <summary>Adds a whole mesh, moved by <paramref name="transform"/>, with its normals and tangents turned to match.</summary>
    public void Add(GeneratedMesh mesh, Matrix4x4 transform)
    {
        Matrix4x4.Invert(transform, out var inverse);
        var normalMatrix = Matrix4x4.Transpose(inverse);
        uint first = (uint)_vertices.Count;
        foreach (var vertex in mesh.Vertices)
        {
            var tangent = Vector3.TransformNormal(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), transform);
            _vertices.Add(vertex with
            {
                Position = Vector3.Transform(vertex.Position, transform),
                Normal = Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, normalMatrix)),
                Tangent = new Vector4(Vector3.Normalize(tangent), vertex.Tangent.W),
            });
        }

        foreach (uint index in mesh.Indices)
        {
            _indices.Add(first + index);
        }
    }

    /// <summary>A two-sided strip up a curve, such as a blade of grass: as wide as each joint's radius says, flat at the tip.</summary>
    public void Strip(IReadOnlyList<Joint> spine, Vector3 across)
    {
        uint first = (uint)_vertices.Count;
        float along = 0f;
        for (int i = 0; i < spine.Count; i++)
        {
            if (i > 0)
            {
                along += Vector3.Distance(spine[i].Position, spine[i - 1].Position);
            }

            var forward = Vector3.Normalize(i + 1 < spine.Count ? spine[i + 1].Position - spine[i].Position : spine[i].Position - spine[i - 1].Position);
            var normal = Vector3.Normalize(Vector3.Cross(across, forward));
            var tangent = new Vector4(across, 1f);
            var offset = across * spine[i].Radius;
            float v = along * uvPerMetre;
            _vertices.Add(new Vertex(spine[i].Position - offset, normal, tangent, new Vector2(0f, v)));
            _vertices.Add(new Vertex(spine[i].Position + offset, normal, tangent, new Vector2(1f, v)));
        }

        for (int i = 0; i + 1 < spine.Count; i++)
        {
            uint a = first + (uint)(i * 2);
            _indices.AddRange([a, a + 1, a + 2, a + 1, a + 3, a + 2]);
        }
    }

    public GeneratedMesh Build(Surface surface) => new([.. _vertices], [.. _indices], surface);

    /// <summary>
    /// Smooth normals from the triangles round each corner, gathered by where the corners are rather than by index, so a
    /// mesh that repeats corners along a seam is smooth across it. Corners facing straight down keep their normal, so a
    /// flat base keeps its crisp edge. Tangents are bent to lie flat on the new normals.
    /// </summary>
    public static void Smooth(Vertex[] vertices, uint[] indices)
    {
        var sums = new Dictionary<Vector3, Vector3>();
        for (int i = 0; i < indices.Length; i += 3)
        {
            var a = vertices[indices[i]].Position;
            var b = vertices[indices[i + 1]].Position;
            var c = vertices[indices[i + 2]].Position;
            var face = Vector3.Cross(b - a, c - a);
            foreach (var corner in (ReadOnlySpan<Vector3>)[a, b, c])
            {
                sums[corner] = sums.GetValueOrDefault(corner) + face;
            }
        }

        foreach (ref var vertex in vertices.AsSpan())
        {
            if (vertex.Normal.Y > -0.99f && sums[vertex.Position] != Vector3.Zero)
            {
                vertex.Normal = Vector3.Normalize(sums[vertex.Position]);
            }

            var tangent = new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);
            tangent = Vector3.Normalize(tangent - vertex.Normal * Vector3.Dot(tangent, vertex.Normal));
            vertex.Tangent = new Vector4(tangent, vertex.Tangent.W);
        }
    }

    private void Ring(Joint joint, Vector3 heading, Vector3 side, int sides, float along)
    {
        var up = Vector3.Cross(heading, side);
        for (int i = 0; i <= sides; i++)
        {
            float angle = MathF.Tau * i / sides;
            var (sin, cos) = MathF.SinCos(angle);
            var outward = side * cos + up * sin;
            var tangent = Vector3.Normalize(Vector3.Cross(heading, outward));
            _vertices.Add(new Vertex(
                joint.Position + outward * joint.Radius,
                outward,
                new Vector4(tangent, 1f),
                new Vector2(i / (float)sides * MathF.Tau * joint.Radius * uvPerMetre, along * uvPerMetre)));
        }
    }
}
