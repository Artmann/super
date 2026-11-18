using System.Numerics;

namespace Rendition;

/// <summary>
/// A lightmap UV set as <see cref="LightmapUnwrap.Problem"/> reads it: each corner's position and UV, and the charts,
/// which are the triangles joined by edges whose corners have the same positions and UVs.
/// </summary>
internal sealed class UvSet
{
    public UvSet(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector2> uvs, ReadOnlySpan<uint> indices)
    {
        Positions = new Vector3[indices.Length];
        Uvs = new Vector2[indices.Length];
        for (int corner = 0; corner < indices.Length; corner++)
        {
            Positions[corner] = positions[(int)indices[corner]];
            Uvs[corner] = uvs[(int)indices[corner]];
        }

        (ChartOf, ChartCount) = FindCharts();
        Members = new List<int>[ChartCount];
        for (int chart = 0; chart < ChartCount; chart++)
        {
            Members[chart] = [];
        }

        for (int t = 0; t < ChartOf.Length; t++)
        {
            Members[ChartOf[t]].Add(t);
        }
    }

    /// <summary>Per corner, its position.</summary>
    public Vector3[] Positions { get; }

    /// <summary>Per corner, its UV.</summary>
    public Vector2[] Uvs { get; }

    /// <summary>Per triangle, its chart, numbered in the order of their first triangles.</summary>
    public int[] ChartOf { get; }

    public int ChartCount { get; }

    /// <summary>Each chart's triangles, in order.</summary>
    public List<int>[] Members { get; }

    /// <summary>The triangles' corners in texels at <paramref name="size"/>, three per triangle in order.</summary>
    public (double[] X, double[] Y) Texels(List<int> triangles, int size)
    {
        var xs = new double[triangles.Count * 3];
        var ys = new double[triangles.Count * 3];
        for (int k = 0; k < triangles.Count; k++)
        {
            for (int i = 0; i < 3; i++)
            {
                var uv = Uvs[triangles[k] * 3 + i];
                xs[k * 3 + i] = uv.X * (double)size;
                ys[k * 3 + i] = uv.Y * (double)size;
            }
        }

        return (xs, ys);
    }

    /// <summary>
    /// The sparsest and densest charts that have world area, with their densities, a chart's UV area over its world
    /// area; or -1 for both when none has.
    /// </summary>
    public (int Sparsest, int Densest, double Low, double High) Densities()
    {
        var (cross, zero) = UnwrapMesh.Areas(Positions);
        int sparsest = -1;
        int densest = -1;
        double low = double.MaxValue;
        double high = double.MinValue;
        for (int chart = 0; chart < ChartCount; chart++)
        {
            double world = UnwrapMesh.WorldArea(cross, zero, Members[chart]);
            if (world <= 0.0)
            {
                continue;
            }

            double density = UvArea(Members[chart], zero) / world;
            if (density < low)
            {
                low = density;
                sparsest = chart;
            }

            if (density > high)
            {
                high = density;
                densest = chart;
            }
        }

        return (sparsest, densest, low, high);
    }

    /// <summary>The triangles' area in the UVs, leaving out those with no world area.</summary>
    private double UvArea(List<int> triangles, bool[] zero)
    {
        double uvArea = 0.0;
        foreach (int t in triangles)
        {
            if (zero[t])
            {
                continue;
            }

            var a = Uvs[t * 3];
            var b = Uvs[t * 3 + 1];
            var c = Uvs[t * 3 + 2];
            uvArea += Math.Abs(((double)b.X - a.X) * ((double)c.Y - a.Y) - ((double)c.X - a.X) * ((double)b.Y - a.Y)) * 0.5;
        }

        return uvArea;
    }

    /// <summary>Numbers the charts in the order of their first triangles.</summary>
    private (int[] ChartOf, int Count) FindCharts()
    {
        var parent = JoinAcrossEdges(CornerIds());
        var chartOf = new int[parent.Length];
        var number = new Dictionary<int, int>();
        for (int t = 0; t < parent.Length; t++)
        {
            int root = Find(parent, t);
            if (!number.TryGetValue(root, out int chart))
            {
                chart = number.Count;
                number.Add(root, chart);
            }

            chartOf[t] = chart;
        }

        return (chartOf, number.Count);
    }

    /// <summary>
    /// Joins triangles across edges whose corners have the same ids, as a union-find forest: per triangle, its
    /// parent.
    /// </summary>
    private static int[] JoinAcrossEdges(int[] id)
    {
        int triangleCount = id.Length / 3;
        var parent = new int[triangleCount];
        for (int t = 0; t < triangleCount; t++)
        {
            parent[t] = t;
        }

        var firstOnEdge = new Dictionary<(int, int), int>();
        for (int corner = 0; corner < id.Length; corner++)
        {
            int a = id[corner];
            int b = id[corner / 3 * 3 + (corner + 1) % 3];
            if (a == b)
            {
                continue;
            }

            var edge = (Math.Min(a, b), Math.Max(a, b));
            if (!firstOnEdge.TryAdd(edge, corner / 3))
            {
                Union(parent, firstOnEdge[edge], corner / 3);
            }
        }

        return parent;
    }

    /// <summary>An id per corner, the same for corners with exactly the same position and UV.</summary>
    private int[] CornerIds()
    {
        var ids = new Dictionary<(Vector3, Vector2), int>();
        var id = new int[Uvs.Length];
        for (int corner = 0; corner < Uvs.Length; corner++)
        {
            var key = (UnwrapMath.Clean(Positions[corner]), new Vector2(Uvs[corner].X + 0f, Uvs[corner].Y + 0f));
            if (!ids.TryGetValue(key, out int value))
            {
                value = ids.Count;
                ids.Add(key, value);
            }

            id[corner] = value;
        }

        return id;
    }

    private static int Find(int[] parent, int t)
    {
        while (parent[t] != t)
        {
            parent[t] = parent[parent[t]];
            t = parent[t];
        }

        return t;
    }

    private static void Union(int[] parent, int a, int b)
    {
        int ra = Find(parent, a);
        int rb = Find(parent, b);
        if (ra != rb)
        {
            parent[Math.Max(ra, rb)] = Math.Min(ra, rb);
        }
    }
}
