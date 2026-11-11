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
    private readonly List<Node> _nodes = [];
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

        var order = new int[triangles.Count];
        var centres = new Vector3[triangles.Count];
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
            var t = triangles[i];
            centres[i] = t.A + (t.Edge1 + t.Edge2) / 3f;
        }

        var keys = new float[order.Length];
        _nodes.Add(default);
        if (order.Length > 0)
        {
            Build(0, 0, order.Length, order, centres, keys, triangles);
        }

        _triangles = new Triangle[order.Length];
        for (int i = 0; i < order.Length; i++)
        {
            _triangles[i] = triangles[order[i]];
        }

        _tree = _nodes.ToArray();
    }

    /// <summary>How many triangles it holds, leaving out those with no area.</summary>
    public int Count => _triangles.Length;

    /// <summary>Whether the ray hits anything closer than <paramref name="maxDistance"/>. <paramref name="direction"/> must be unit length.</summary>
    public bool Any(Vector3 origin, Vector3 direction, float maxDistance) =>
        Trace(origin, direction, maxDistance, anyHit: true, out _, out _);

    /// <summary>
    /// The nearest hit closer than <paramref name="maxDistance"/>: how far along the ray it is and the normal of the
    /// triangle it hits, by its winding. <paramref name="direction"/> must be unit length.
    /// </summary>
    public bool Closest(Vector3 origin, Vector3 direction, float maxDistance, out float distance, out Vector3 normal) =>
        Trace(origin, direction, maxDistance, anyHit: false, out distance, out normal);

    private bool Trace(Vector3 origin, Vector3 direction, float maxDistance, bool anyHit, out float distance, out Vector3 normal)
    {
        distance = maxDistance;
        normal = default;
        if (_triangles.Length == 0)
        {
            return false;
        }

        var inverse = new Vector3(1f / direction.X, 1f / direction.Y, 1f / direction.Z);
        bool hit = false;
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

            for (int i = node.First; i < node.First + node.Count; i++)
            {
                if (Intersect(_triangles[i], origin, direction, distance, out float t))
                {
                    distance = t;
                    normal = _triangles[i].Normal;
                    hit = true;
                    if (anyHit)
                    {
                        return true;
                    }
                }
            }
        }

        return hit;
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

    // Splits the triangles at the middle of their centres along the box's longest side, until few are left.
    private void Build(int index, int first, int count, int[] order, Vector3[] centres, float[] keys, List<Triangle> triangles)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        var centreMin = new Vector3(float.MaxValue);
        var centreMax = new Vector3(float.MinValue);
        for (int i = first; i < first + count; i++)
        {
            var t = triangles[order[i]];
            var b = t.A + t.Edge1;
            var c = t.A + t.Edge2;
            min = Vector3.Min(min, Vector3.Min(t.A, Vector3.Min(b, c)));
            max = Vector3.Max(max, Vector3.Max(t.A, Vector3.Max(b, c)));
            centreMin = Vector3.Min(centreMin, centres[order[i]]);
            centreMax = Vector3.Max(centreMax, centres[order[i]]);
        }

        if (count <= LeafSize)
        {
            _nodes[index] = new Node { Min = min, Max = max, First = first, Count = count };
            return;
        }

        var extent = centreMax - centreMin;
        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        for (int i = first; i < first + count; i++)
        {
            keys[i] = centres[order[i]][axis];
        }

        Array.Sort(keys, order, first, count);
        int half = count / 2;
        int children = _nodes.Count;
        _nodes.Add(default);
        _nodes.Add(default);
        _nodes[index] = new Node { Min = min, Max = max, First = children, Count = 0 };
        Build(children, first, half, order, centres, keys, triangles);
        Build(children + 1, first + half, count - half, order, centres, keys, triangles);
    }
}
