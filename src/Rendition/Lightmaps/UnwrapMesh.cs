using System.Numerics;

namespace Rendition;

/// <summary>A chart laid flat: three corners per triangle, in the chart's order.</summary>
internal readonly record struct FlatChart(double[] X, double[] Y);

/// <summary>The mesh as the unwrapper reads it: corners welded by position, areas and face normals.</summary>
internal sealed class UnwrapMesh
{
    /// <summary>Vertex normals within about 5 degrees agree; more is a hard edge.</summary>
    private const float NormalAgreement = 0.996f;

    /// <summary>A triangle with less area than this share of the mesh's largest has none.</summary>
    private const double ZeroArea = 1e-10;

    public readonly Vertex[] Vertices;
    public readonly uint[] Indices;
    public readonly int TriangleCount;

    /// <summary>Per corner, its position.</summary>
    public readonly Vector3[] Corners;

    /// <summary>Per triangle, the cross product of two of its edges: the face normal times twice the area.</summary>
    public readonly Vector3D[] Cross;

    public readonly bool[] Zero;

    /// <summary>Per corner, the id of its position; corners at the same place share one.</summary>
    private readonly int[] positionId;

    private readonly EdgeGroups edges;

    public UnwrapMesh(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices)
    {
        CheckIndices(indices, vertices.Length);
        Vertices = vertices.ToArray();
        Indices = indices.ToArray();
        TriangleCount = indices.Length / 3;
        Corners = new Vector3[Indices.Length];
        for (int corner = 0; corner < Indices.Length; corner++)
        {
            Corners[corner] = Vertices[Indices[corner]].Position;
        }

        positionId = Weld(Corners);
        (Cross, Zero) = Areas(Corners);
        edges = new EdgeGroups(positionId);
    }

    /// <summary>Throws unless the indices make whole triangles of the <paramref name="vertexCount"/> vertices.</summary>
    public static void CheckIndices(ReadOnlySpan<uint> indices, int vertexCount)
    {
        if (indices.Length % 3 != 0)
        {
            throw new ArgumentException($"There are {indices.Length} indices, which isn't a whole number of triangles.", nameof(indices));
        }

        foreach (uint index in indices)
        {
            if (index >= vertexCount)
            {
                throw new ArgumentException($"Index {index} is past the {vertexCount} vertices.", nameof(indices));
            }
        }
    }

    /// <summary>
    /// The corners whose edge to the next corner joins the same two positions as the corner's, itself included;
    /// none when its edge joins a position to itself.
    /// </summary>
    public ReadOnlySpan<int> Partners(int corner) => edges.Partners(corner);

    /// <summary>The first triangle across an edge that has a chart in <paramref name="chartOf"/>, or -1.</summary>
    public int FirstNeighbourWithChart(int triangle, int[] chartOf)
    {
        for (int corner = triangle * 3; corner < triangle * 3 + 3; corner++)
        {
            foreach (int other in Partners(corner))
            {
                if (other / 3 != triangle && chartOf[other / 3] >= 0)
                {
                    return other / 3;
                }
            }
        }

        return -1;
    }

    /// <summary>Whether the vertex normals at both ends of the edge agree on both sides of it.</summary>
    public bool SmoothEdge(int corner, int other)
    {
        int cornerNext = corner / 3 * 3 + (corner + 1) % 3;
        int otherNext = other / 3 * 3 + (other + 1) % 3;
        if (positionId[corner] == positionId[other])
        {
            return Agree(corner, other) && Agree(cornerNext, otherNext);
        }

        return Agree(corner, otherNext) && Agree(cornerNext, other);
    }

    /// <summary>The triangles' cross products added up: their mean normal times twice their area.</summary>
    public Vector3D Sum(List<int> members)
    {
        var sum = default(Vector3D);
        foreach (int triangle in members)
        {
            sum += Cross[triangle];
        }

        return sum;
    }

    /// <summary>The triangles' area in square metres, leaving out those with none.</summary>
    public static double WorldArea(Vector3D[] cross, bool[] zero, List<int> members)
    {
        double world = 0.0;
        foreach (int triangle in members)
        {
            if (!zero[triangle])
            {
                world += cross[triangle].Length() * 0.5;
            }
        }

        return world;
    }

    /// <summary>
    /// Lays the triangles onto the plane of their area-weighted mean normal, three corners each in order, measured
    /// from the first corner so far-off meshes keep their precision.
    /// </summary>
    public FlatChart Flatten(List<int> members)
    {
        var sum = Sum(members);
        double length = sum.Length();
        var normal = length > 0.0 ? sum / length : new Vector3D(0.0, 0.0, 1.0);
        var helper = Math.Abs(normal.X) < 0.7 ? new Vector3D(1.0, 0.0, 0.0) : new Vector3D(0.0, 1.0, 0.0);
        var u = Vector3D.Cross(helper, normal).Normalized();
        var v = Vector3D.Cross(normal, u);

        var origin = new Vector3D(Corners[members[0] * 3]);
        var xs = new double[members.Count * 3];
        var ys = new double[members.Count * 3];
        for (int k = 0; k < members.Count; k++)
        {
            for (int i = 0; i < 3; i++)
            {
                var p = new Vector3D(Corners[members[k] * 3 + i]) - origin;
                xs[k * 3 + i] = Vector3D.Dot(p, u);
                ys[k * 3 + i] = Vector3D.Dot(p, v);
            }
        }

        return new FlatChart(xs, ys);
    }

    /// <summary>Each triangle's cross product, and whether it has no area next to the largest.</summary>
    public static (Vector3D[] Cross, bool[] Zero) Areas(Vector3[] corners)
    {
        int count = corners.Length / 3;
        var cross = new Vector3D[count];
        double largest = 0.0;
        for (int t = 0; t < count; t++)
        {
            var a = new Vector3D(corners[t * 3]);
            cross[t] = Vector3D.Cross(new Vector3D(corners[t * 3 + 1]) - a, new Vector3D(corners[t * 3 + 2]) - a);
            largest = Math.Max(largest, cross[t].Length());
        }

        var zero = new bool[count];
        for (int t = 0; t < count; t++)
        {
            double length = cross[t].Length();
            zero[t] = length <= 0.0 || length <= largest * ZeroArea;
        }

        return (cross, zero);
    }

    private bool Agree(int a, int b)
    {
        var na = Vertices[Indices[a]].Normal;
        var nb = Vertices[Indices[b]].Normal;
        float la = na.LengthSquared();
        float lb = nb.LengthSquared();
        if (la < 1e-12f || lb < 1e-12f)
        {
            return la < 1e-12f && lb < 1e-12f;
        }

        return Vector3.Dot(na, nb) >= NormalAgreement * MathF.Sqrt(la * lb);
    }

    /// <summary>An id per corner, the same for corners at exactly the same position.</summary>
    private static int[] Weld(Vector3[] corners)
    {
        var ids = new Dictionary<Vector3, int>();
        var id = new int[corners.Length];
        for (int corner = 0; corner < corners.Length; corner++)
        {
            var key = UnwrapMath.Clean(corners[corner]);
            if (!ids.TryGetValue(key, out int value))
            {
                value = ids.Count;
                ids.Add(key, value);
            }

            id[corner] = value;
        }

        return id;
    }
}

/// <summary>The corners grouped by edge: those whose edge to the next corner joins the same two positions.</summary>
internal sealed class EdgeGroups
{
    /// <summary>Per corner, its group, or -1 when its edge joins a position to itself.</summary>
    private readonly int[] edgeGroup;

    /// <summary>Group g holds <c>groupCorners[groupStart[g]]</c> up to <c>groupCorners[groupStart[g + 1]]</c>.</summary>
    private readonly int[] groupStart;
    private readonly int[] groupCorners;

    /// <param name="positionId">Per corner, the id of its position.</param>
    public EdgeGroups(int[] positionId)
    {
        var groups = new Dictionary<(int, int), int>();
        edgeGroup = new int[positionId.Length];
        var entries = new List<(int Bucket, int Item)>(positionId.Length);
        for (int corner = 0; corner < positionId.Length; corner++)
        {
            int a = positionId[corner];
            int b = positionId[corner / 3 * 3 + (corner + 1) % 3];
            if (a == b)
            {
                edgeGroup[corner] = -1;
                continue;
            }

            var key = (Math.Min(a, b), Math.Max(a, b));
            if (!groups.TryGetValue(key, out int group))
            {
                group = groups.Count;
                groups.Add(key, group);
            }

            edgeGroup[corner] = group;
            entries.Add((group, corner));
        }

        (groupStart, groupCorners) = UnwrapMath.Bucket(entries, groups.Count);
    }

    /// <summary>The corners in the corner's group, itself included, or none when it has no group.</summary>
    public ReadOnlySpan<int> Partners(int corner)
    {
        int group = edgeGroup[corner];
        if (group < 0)
        {
            return [];
        }

        return groupCorners.AsSpan(groupStart[group], groupStart[group + 1] - groupStart[group]);
    }
}
