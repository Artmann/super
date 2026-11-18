using System.Numerics;

namespace Super.Cook.Models;

/// <summary>
/// A bounding volume hierarchy over triangles, so a ray finds what it hits without testing every one. Built once, then
/// safe to query from many threads. Triangles count from either side.
/// </summary>
public sealed class Bvh
{
    private const int LeafSize = 4;

    private readonly Triangle[] _triangles;
    private readonly Node[] _tree;

    private readonly struct Triangle(Vector3 a, Vector3 b, Vector3 c)
    {
        public readonly Vector3 A = a;
        public readonly Vector3 Edge1 = b - a;
        public readonly Vector3 Edge2 = c - a;
        public readonly Vector3 Normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
    }

    // A leaf holds Count triangles from First on. An inner node has Count 0 and its children at First and First + 1.
    private struct Node
    {
        public Vector3 Min;
        public Vector3 Max;
        public int First;
        public int Count;
    }

    /// <summary>Builds the hierarchy over the triangles <paramref name="indices"/> names, three corners each.</summary>
    public Bvh(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices)
    {
        var triangles = new List<Triangle>(indices.Length / 3);
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            var a = positions[indices[i]];
            var b = positions[indices[i + 1]];
            var c = positions[indices[i + 2]];
            // A triangle with no area can't be hit, and has no normal.
            if (Vector3.Cross(b - a, c - a).LengthSquared() > 1e-20f)
            {
                triangles.Add(new Triangle(a, b, c));
            }
        }

        var builder = new Builder(triangles);
        var order = builder.Order;
        _triangles = new Triangle[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            _triangles[i] = triangles[order[i]];
        }

        _tree = builder.Nodes.ToArray();
    }

    /// <summary>How many triangles it holds, leaving out those with no area.</summary>
    public int Count => _triangles.Length;

    /// <summary>Whether the ray hits anything closer than <paramref name="maxDistance"/>. <paramref name="direction"/> must be unit length.</summary>
    public bool Any(Vector3 origin, Vector3 direction, float maxDistance) =>
        Trace(origin, direction, maxDistance, anyHit: true, out _) >= 0;

    /// <summary>
    /// The nearest hit closer than <paramref name="maxDistance"/>: how far along the ray it is and the normal of the
    /// triangle it hits, by its winding. <paramref name="direction"/> must be unit length.
    /// </summary>
    public bool Closest(Vector3 origin, Vector3 direction, float maxDistance, out float distance, out Vector3 normal)
    {
        int found = Trace(origin, direction, maxDistance, anyHit: false, out distance);
        normal = found >= 0 ? _triangles[found].Normal : default;
        return found >= 0;
    }

    /// <summary>The triangle the ray hits nearest, or with <paramref name="anyHit"/> any it hits, or -1 for none.</summary>
    private int Trace(Vector3 origin, Vector3 direction, float maxDistance, bool anyHit, out float distance)
    {
        distance = maxDistance;
        if (_triangles.Length == 0)
        {
            return -1;
        }

        var inverse = new Vector3(1f / direction.X, 1f / direction.Y, 1f / direction.Z);
        int last = -1;
        Span<int> stack = stackalloc int[64];
        int top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            ref readonly var node = ref _tree[stack[--top]];
            if (!Enters(node.Min, node.Max, origin, inverse, distance))
            {
                continue;
            }

            if (node.Count == 0)
            {
                stack[top++] = node.First;
                stack[top++] = node.First + 1;
                continue;
            }

            int found = HitInLeaf(node, origin, direction, ref distance, anyHit);
            if (found >= 0)
            {
                last = found;
                if (anyHit)
                {
                    return last;
                }
            }
        }

        return last;
    }

    /// <summary>
    /// The last of a leaf's triangles the ray hits closer than <paramref name="distance"/>, which moves in to each hit,
    /// or -1 when it hits none. With <paramref name="anyHit"/>, the first it hits.
    /// </summary>
    private int HitInLeaf(in Node node, Vector3 origin, Vector3 direction, ref float distance, bool anyHit)
    {
        int found = -1;
        for (int i = node.First; i < node.First + node.Count; i++)
        {
            if (Intersect(_triangles[i], origin, direction, distance, out float t))
            {
                distance = t;
                found = i;
                if (anyHit)
                {
                    return found;
                }
            }
        }

        return found;
    }

    // Möller and Trumbore's test, from either side.
    private static bool Intersect(in Triangle triangle, Vector3 origin, Vector3 direction, float maxDistance, out float distance)
    {
        distance = 0f;
        var p = Vector3.Cross(direction, triangle.Edge2);
        float determinant = Vector3.Dot(triangle.Edge1, p);
        if (MathF.Abs(determinant) < 1e-12f)
        {
            return false;
        }

        float inverse = 1f / determinant;
        var s = origin - triangle.A;
        float u = Vector3.Dot(s, p) * inverse;
        if (u < 0f || u > 1f)
        {
            return false;
        }

        var q = Vector3.Cross(s, triangle.Edge1);
        float v = Vector3.Dot(direction, q) * inverse;
        if (v < 0f || u + v > 1f)
        {
            return false;
        }

        distance = Vector3.Dot(triangle.Edge2, q) * inverse;
        return distance > 1e-6f && distance < maxDistance;
    }

    // The slab test: whether the ray passes through the box before maxDistance.
    private static bool Enters(Vector3 min, Vector3 max, Vector3 origin, Vector3 inverse, float maxDistance)
    {
        var t0 = (min - origin) * inverse;
        var t1 = (max - origin) * inverse;
        var near = Vector3.Min(t0, t1);
        var far = Vector3.Max(t0, t1);
        float enter = MathF.Max(MathF.Max(near.X, near.Y), MathF.Max(near.Z, 0f));
        float exit = MathF.Min(MathF.Min(far.X, far.Y), MathF.Min(far.Z, maxDistance));
        // A ray along a box's face gives NaN, which counts as entering: a wasted look inside, never a missed hit.
        return !(enter > exit);
    }

    /// <summary>
    /// Builds the tree's nodes over the triangles, and the order the leaves hold them in. Splits the triangles at the
    /// middle of their centres along the box's longest side, until few are left.
    /// </summary>
    private sealed class Builder
    {
        private readonly List<Triangle> _triangles;
        private readonly Vector3[] _centres;
        private readonly float[] _keys;

        public Builder(List<Triangle> triangles)
        {
            _triangles = triangles;
            Order = new int[triangles.Count];
            _centres = new Vector3[triangles.Count];
            for (int i = 0; i < Order.Length; i++)
            {
                Order[i] = i;
                var t = triangles[i];
                _centres[i] = t.A + (t.Edge1 + t.Edge2) / 3f;
            }

            _keys = new float[Order.Length];
            Nodes.Add(default);
            if (Order.Length > 0)
            {
                Split(0, 0, Order.Length);
            }
        }

        /// <summary>The triangles in the order the leaves hold them, by their index in the list given.</summary>
        public int[] Order { get; }

        public List<Node> Nodes { get; } = [];

        private void Split(int index, int first, int count)
        {
            var (min, max, centreMin, centreMax) = Bounds(first, count);
            if (count <= LeafSize)
            {
                Nodes[index] = new Node { Min = min, Max = max, First = first, Count = count };
                return;
            }

            var extent = centreMax - centreMin;
            int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
            for (int i = first; i < first + count; i++)
            {
                _keys[i] = _centres[Order[i]][axis];
            }

            Array.Sort(_keys, Order, first, count);
            int half = count / 2;
            int children = Nodes.Count;
            Nodes.Add(default);
            Nodes.Add(default);
            Nodes[index] = new Node { Min = min, Max = max, First = children, Count = 0 };
            Split(children, first, half);
            Split(children + 1, first + half, count - half);
        }

        /// <summary>The box round the triangles from <paramref name="first"/> on, and the box round their centres.</summary>
        private (Vector3 Min, Vector3 Max, Vector3 CentreMin, Vector3 CentreMax) Bounds(int first, int count)
        {
            var min = new Vector3(float.MaxValue);
            var max = new Vector3(float.MinValue);
            var centreMin = new Vector3(float.MaxValue);
            var centreMax = new Vector3(float.MinValue);
            for (int i = first; i < first + count; i++)
            {
                var t = _triangles[Order[i]];
                var b = t.A + t.Edge1;
                var c = t.A + t.Edge2;
                min = Vector3.Min(min, Vector3.Min(t.A, Vector3.Min(b, c)));
                max = Vector3.Max(max, Vector3.Max(t.A, Vector3.Max(b, c)));
                centreMin = Vector3.Min(centreMin, _centres[Order[i]]);
                centreMax = Vector3.Max(centreMax, _centres[Order[i]]);
            }

            return (min, max, centreMin, centreMax);
        }
    }
}
