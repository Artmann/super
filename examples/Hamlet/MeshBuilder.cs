using System.Numerics;
using Rendition;
using Super;

namespace Hamlet;

/// <summary>
/// Gathers many shapes into one mesh with one surface, each moved into place first. A
/// building is a handful of these, one per material, so it draws in a few calls however
/// many boxes it's made of.
/// </summary>
internal sealed class MeshBuilder
{
    private readonly List<Vertex> _vertices = [];
    private readonly List<uint> _indices = [];

    /// <summary>Whether nothing has been added yet.</summary>
    public bool IsEmpty => _indices.Count == 0;

    /// <summary>Adds a mesh, moved by the transform. Scaling is allowed; mirroring is not.</summary>
    public void Add(GeneratedMesh mesh, Matrix4x4 transform)
    {
        // Normals follow the inverse transpose, which keeps them square to a stretched surface.
        Matrix4x4.Invert(transform, out var inverse);
        var normals = Matrix4x4.Transpose(inverse);

        uint first = (uint)_vertices.Count;
        foreach (var vertex in mesh.Vertices)
        {
            var moved = vertex;
            moved.Position = Vector3.Transform(vertex.Position, transform);
            moved.Normal = Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, normals));
            var tangent = Vector3.TransformNormal(new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z), transform);
            moved.Tangent = new Vector4(Vector3.Normalize(tangent), vertex.Tangent.W);
            _vertices.Add(moved);
        }

        foreach (uint index in mesh.Indices)
        {
            _indices.Add(first + index);
        }
    }

    /// <summary>A box of this size with its middle at the transform's origin.</summary>
    public void Box(Vector3 size, Matrix4x4 transform) => Add(Shapes.Box(size), transform);

    /// <summary>A box of this size with its middle at a point, lined up with the axes.</summary>
    public void Box(Vector3 size, Vector3 middle) => Box(size, Matrix4x4.CreateTranslation(middle));

    /// <summary>A box spanning from one corner to the other, lined up with the axes.</summary>
    public void Span(Vector3 from, Vector3 to, Matrix4x4 frame)
    {
        var low = Vector3.Min(from, to);
        var high = Vector3.Max(from, to);
        var size = high - low;
        if (size.X <= 1e-4f || size.Y <= 1e-4f || size.Z <= 1e-4f)
        {
            return;
        }

        Box(size, Matrix4x4.CreateTranslation((low + high) * 0.5f) * frame);
    }

    /// <summary>
    /// A triangular block for the end of a pitched roof: its base lies along x and z from
    /// -half to +half wide, at height zero, rising to a ridge of the given height, and it runs
    /// the given length along x. The frame then moves and turns it.
    /// </summary>
    public void Gable(float halfWidth, float height, float length, Matrix4x4 frame)
    {
        // A three-sided prism stands on y with its first corner along x. Turned a quarter
        // round z, that corner points up and the prism lies along x.
        var prism = Shapes.Prism(3, 1f, 1f);
        var shape = Matrix4x4.CreateRotationZ(MathF.PI / 2f)
            * Matrix4x4.CreateScale(length, height / 1.5f, halfWidth / 0.8660254f)
            * Matrix4x4.CreateTranslation(0f, height / 3f, 0f);
        Add(prism, shape * frame);
    }

    /// <summary>
    /// The two slopes of a pitched roof with its ridge along x, sitting on walls whose tops
    /// are at height zero and run from -halfSpan to +halfSpan in z. They hang over the walls
    /// by the overhang at the eaves and at both ends.
    /// </summary>
    public void PitchedRoof(float halfSpan, float rise, float length, float overhang, float thickness, Matrix4x4 frame)
    {
        float angle = MathF.Atan2(rise, halfSpan);
        float run = halfSpan + overhang;
        float slope = run / MathF.Cos(angle) + thickness;
        foreach (float side in (ReadOnlySpan<float>)[1f, -1f])
        {
            var ridge = new Vector3(0f, rise, 0f);
            var eave = new Vector3(0f, -overhang * rise / halfSpan, side * run);
            var normal = new Vector3(0f, MathF.Cos(angle), side * MathF.Sin(angle));
            var middle = (ridge + eave) * 0.5f + normal * (thickness * 0.5f);
            var place = Matrix4x4.CreateRotationX(side * angle) * Matrix4x4.CreateTranslation(middle) * frame;
            Box(new Vector3(length + overhang * 2f, thickness, slope), place);
        }
    }

    /// <summary>The finished mesh, with this surface.</summary>
    public GeneratedMesh Build(Surface surface) => new([.. _vertices], [.. _indices], surface);
}
