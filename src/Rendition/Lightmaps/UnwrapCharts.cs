namespace Rendition;

/// <summary>
/// The charts a mesh is unwrapped in. They grow over edges whose corners are at the same positions with agreeing
/// normals, taking triangles within 60 degrees of the chart's running mean normal, and are cut back where their
/// flattening overlaps itself, with the rest grown again from there. Triangles with no area join a neighbour's chart,
/// or make one of their own.
/// </summary>
internal sealed class UnwrapCharts
{
    /// <summary>A chart takes triangles within 60 degrees of its mean normal.</summary>
    private const double ConeCosine = 0.5;

    private readonly UnwrapMesh mesh;
    private readonly ChartCutter cutter;

    private UnwrapCharts(UnwrapMesh mesh)
    {
        this.mesh = mesh;
        ChartOf = new int[mesh.TriangleCount];
        Array.Fill(ChartOf, -1);
        cutter = new ChartCutter(mesh, ChartOf);
    }

    /// <summary>Per triangle, its chart.</summary>
    public int[] ChartOf { get; }

    /// <summary>Each chart's triangles, in the order it grew.</summary>
    public List<List<int>> Members { get; } = [];

    public static UnwrapCharts Build(UnwrapMesh mesh)
    {
        var charts = new UnwrapCharts(mesh);
        charts.ChartFree([]);
        return charts;
    }

    /// <summary>
    /// Cuts each chart with two triangles that share a texel while far apart back to before the later of them, and
    /// charts what was cut again.
    /// </summary>
    public void CutBack(List<FarShare> shares)
    {
        var pending = new Stack<int>();
        foreach (var (chart, cut) in CutPoints(shares).OrderByDescending(pair => pair.Key))
        {
            var members = Members[chart];
            var freed = new List<int>();
            ChartCutter.CutFrom(members, cut, ChartOf, freed);
            cutter.Settle(members, pending);
            ChartCutter.PushReversed(pending, freed);
        }

        ChartFree(pending);
    }

    /// <summary>Per chart with a far share, the earliest place it grew one of its later triangles, never 0.</summary>
    private Dictionary<int, int> CutPoints(List<FarShare> shares)
    {
        var rank = new int[mesh.TriangleCount];
        foreach (var members in Members)
        {
            for (int k = 0; k < members.Count; k++)
            {
                rank[members[k]] = k;
            }
        }

        var cutAt = new Dictionary<int, int>();
        foreach (var share in shares)
        {
            int chart = ChartOf[share.A];
            int later = Math.Max(1, Math.Max(rank[share.A], rank[share.B]));
            cutAt[chart] = cutAt.TryGetValue(chart, out int at) ? Math.Min(at, later) : later;
        }

        return cutAt;
    }

    /// <summary>
    /// Charts every triangle that has no chart yet: those with area grow into new charts, seeded first from
    /// <paramref name="pending"/> and then in index order, and those without join a neighbour's chart or make one.
    /// </summary>
    private void ChartFree(Stack<int> pending)
    {
        int next = 0;
        while (true)
        {
            int seed = PopSeed(pending);
            if (seed < 0)
            {
                next = NextSeed(next);
                if (next == mesh.TriangleCount)
                {
                    break;
                }

                seed = next;
            }

            var members = Grow(seed, Members.Count);
            Members.Add(members);
            cutter.Settle(members, pending);
        }

        JoinNeighbours();
        for (int triangle = 0; triangle < mesh.TriangleCount; triangle++)
        {
            if (ChartOf[triangle] < 0)
            {
                ChartOf[triangle] = Members.Count;
                Members.Add([triangle]);
            }
        }
    }

    /// <summary>The first triangle on <paramref name="pending"/> that has area and no chart, or -1.</summary>
    private int PopSeed(Stack<int> pending)
    {
        while (pending.Count > 0)
        {
            int candidate = pending.Pop();
            if (ChartOf[candidate] < 0 && !mesh.Zero[candidate])
            {
                return candidate;
            }
        }

        return -1;
    }

    /// <summary>The first triangle from <paramref name="from"/> on that has area and no chart, or the triangle count.</summary>
    private int NextSeed(int from)
    {
        while (from < mesh.TriangleCount && (ChartOf[from] >= 0 || mesh.Zero[from]))
        {
            from++;
        }

        return from;
    }

    /// <summary>Triangles with no area take a neighbour's chart; chains of them pass it along, breadth-first.</summary>
    private void JoinNeighbours()
    {
        var queue = new Queue<int>();
        for (int triangle = 0; triangle < mesh.TriangleCount; triangle++)
        {
            if (ChartOf[triangle] < 0 && mesh.FirstNeighbourWithChart(triangle, ChartOf) >= 0)
            {
                queue.Enqueue(triangle);
            }
        }

        while (queue.Count > 0)
        {
            int triangle = queue.Dequeue();
            if (ChartOf[triangle] >= 0)
            {
                continue;
            }

            int neighbour = mesh.FirstNeighbourWithChart(triangle, ChartOf);
            ChartOf[triangle] = ChartOf[neighbour];
            Members[ChartOf[triangle]].Add(triangle);
            EnqueueUncharted(queue, triangle);
        }
    }

    /// <summary>Queues the triangles across the triangle's edges that have no chart, edge by edge.</summary>
    private void EnqueueUncharted(Queue<int> queue, int triangle)
    {
        for (int corner = triangle * 3; corner < triangle * 3 + 3; corner++)
        {
            foreach (int other in mesh.Partners(corner))
            {
                if (other / 3 != triangle && ChartOf[other / 3] < 0)
                {
                    queue.Enqueue(other / 3);
                }
            }
        }
    }

    private List<int> Grow(int seed, int chart)
    {
        var members = new List<int> { seed };
        ChartOf[seed] = chart;
        var sum = mesh.Cross[seed];
        for (int k = 0; k < members.Count; k++)
        {
            int first = members[k] * 3;
            for (int corner = first; corner < first + 3; corner++)
            {
                foreach (int other in mesh.Partners(corner))
                {
                    if (Joins(corner, other, sum))
                    {
                        int neighbour = other / 3;
                        ChartOf[neighbour] = chart;
                        members.Add(neighbour);
                        sum += mesh.Cross[neighbour];
                    }
                }
            }
        }

        return members;
    }

    /// <summary>
    /// Whether the triangle of <paramref name="other"/> joins the chart across the edge from <paramref name="corner"/>:
    /// it has area and no chart, the edge is smooth, and it is within 60 degrees of the chart's mean normal. The
    /// corner's own triangle is in the chart already, so it never joins again.
    /// </summary>
    private bool Joins(int corner, int other, Vector3D sum)
    {
        int neighbour = other / 3;
        if (ChartOf[neighbour] >= 0 || mesh.Zero[neighbour] || !mesh.SmoothEdge(corner, other))
        {
            return false;
        }

        var face = mesh.Cross[neighbour];
        return !(Vector3D.Dot(face, sum) < ConeCosine * face.Length() * sum.Length());
    }
}
