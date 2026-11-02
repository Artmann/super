using System.Numerics;

namespace Rendition;

/// <summary>
/// Lights a tree's crown as one rounded shape. Leaf cards have the normals of flat
/// cards, so each one is lit on its own and the crown looks speckled. Pointing every
/// normal out of the crown, as most engines do, makes it look full.
/// </summary>
public static class FoliageNormals
{
    /// <summary>
    /// Points each normal out of the egg shape that fills the leaves' bounds, so a tall
    /// pine's sides face sideways and a wide crown's top faces up. Tangents turn to stay
    /// at right angles to their normal and keep their handedness. Pass only the crown's
    /// vertices, without the trunk.
    /// </summary>
    public static void PointOutOfCrown(Span<Vertex> leaves)
    {
        if (leaves.IsEmpty)
        {
            return;
        }

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (ref readonly var vertex in leaves)
        {
            min = Vector3.Min(min, vertex.Position);
            max = Vector3.Max(max, vertex.Position);
        }

        var center = (min + max) * 0.5f;

        // A flat crown, such as a single card, has no depth on one axis. A floor on the
        // radii keeps the division finite and points those normals along the thin axis.
        var radii = (max - min) * 0.5f;
        float floor = MathF.Max(MathF.Max(radii.X, MathF.Max(radii.Y, radii.Z)) * 1e-3f, 1e-6f);
        radii = Vector3.Max(radii, new Vector3(floor));
        var inverseSquared = Vector3.One / (radii * radii);

        foreach (ref var vertex in leaves)
        {
            // The gradient of the egg shape at this point is its outward normal.
            var outward = (vertex.Position - center) * inverseSquared;
            var normal = outward.LengthSquared() > 0f ? Vector3.Normalize(outward) : Vector3.UnitY;
            vertex.Normal = normal;

            var tangent = new Vector3(vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z);
            tangent -= normal * Vector3.Dot(normal, tangent);
            if (tangent.LengthSquared() < 1e-12f)
            {
                // The old tangent ran along the new normal, so any right angle will do.
                var axis = MathF.Abs(normal.Y) < 0.99f ? Vector3.UnitY : Vector3.UnitX;
                tangent = Vector3.Cross(axis, normal);
            }

            vertex.Tangent = new Vector4(Vector3.Normalize(tangent), vertex.Tangent.W);
        }
    }
}
