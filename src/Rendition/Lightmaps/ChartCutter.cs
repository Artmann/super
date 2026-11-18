namespace Rendition;

/// <summary>
/// Cuts a grown chart until it is fine: triangles more than 75 degrees from its mean normal leave it, and so does
/// everything grown after the later of two triangles that overlap once it's laid flat.
/// </summary>
internal sealed class ChartCutter(UnwrapMesh mesh, int[] chartOf)
{
    /// <summary>
    /// Triangles of one flattened chart overlap when they share more than this share of the chart's extent squared.
    /// Far stricter than the hundredth of a texel <see cref="LightmapUnwrap.Problem"/> allows, so what the unwrapper
    /// accepts the check does too.
    /// </summary>
    private const double ProjectedOverlap = 1e-12;

    /// <summary>A finished chart keeps no triangle more than 75 degrees from its mean normal.</summary>
    private const double SteepCosine = 0.2588;

    /// <summary>
    /// Cuts the chart until it is fine. Whatever leaves goes on <paramref name="pending"/>, first-grown on top, to
    /// grow into charts of its own.
    /// </summary>
    public void Settle(List<int> members, Stack<int> pending)
    {
        int chart = chartOf[members[0]];
        var freed = new List<int>();
        while (true)
        {
            if (DropSteep(members, chart, freed))
            {
                continue;
            }

            int cut = FirstBadRank(members);
            if (cut < 0)
            {
                break;
            }

            CutFrom(members, cut, chartOf, freed);
        }

        PushReversed(pending, freed);
    }

    /// <summary>Takes the chart's triangles from <paramref name="cut"/> on out of it, onto the end of <paramref name="freed"/>.</summary>
    public static void CutFrom(List<int> members, int cut, int[] chartOf, List<int> freed)
    {
        for (int k = cut; k < members.Count; k++)
        {
            chartOf[members[k]] = -1;
            freed.Add(members[k]);
        }

        members.RemoveRange(cut, members.Count - cut);
    }

    /// <summary>Pushes the triangles last to first, so the first is on top.</summary>
    public static void PushReversed(Stack<int> pending, List<int> freed)
    {
        for (int k = freed.Count - 1; k >= 0; k--)
        {
            pending.Push(freed[k]);
        }
    }

    /// <summary>
    /// Takes out of the chart the triangles more than 75 degrees from its mean normal, which flattening would squash
    /// to under a quarter of their density, and then whatever no longer joins up with the rest. The rest is put back in
    /// breadth-first order, so every first part of it is still joined up. Whether anything was taken out.
    /// </summary>
    private bool DropSteep(List<int> members, int chart, List<int> freed)
    {
        int freedBefore = freed.Count;
        int first = FreeSteep(members, freed);
        if (freed.Count == freedBefore)
        {
            return false;
        }

        if (first < 0)
        {
            // Every triangle with area is steep, which a 60-degree cone can't grow; keep the seed.
            first = members[0];
            freed.Remove(first);
            chartOf[first] = chart;
        }

        var (order, reached) = ReachSmooth(first, chart);
        foreach (int triangle in members)
        {
            if (chartOf[triangle] == chart && !reached.Contains(triangle))
            {
                chartOf[triangle] = -1;
                freed.Add(triangle);
            }
        }

        members.Clear();
        members.AddRange(order);
        return true;
    }

    /// <summary>
    /// Frees the triangles with area that are more than 75 degrees from the chart's mean normal. The first triangle
    /// with area that stays, or -1 when none does.
    /// </summary>
    private int FreeSteep(List<int> members, List<int> freed)
    {
        var sum = mesh.Sum(members);
        double sumLength = sum.Length();
        int first = -1;
        foreach (int triangle in members)
        {
            var face = mesh.Cross[triangle];
            if (mesh.Zero[triangle] || Vector3D.Dot(face, sum) >= SteepCosine * face.Length() * sumLength)
            {
                if (first < 0 && !mesh.Zero[triangle])
                {
                    first = triangle;
                }
            }
            else
            {
                chartOf[triangle] = -1;
                freed.Add(triangle);
            }
        }

        return first;
    }

    /// <summary>Breadth-first from <paramref name="first"/>, across smooth edges, over what's still in the chart.</summary>
    private (List<int> Order, HashSet<int> Reached) ReachSmooth(int first, int chart)
    {
        var order = new List<int> { first };
        var reached = new HashSet<int> { first };
        for (int k = 0; k < order.Count; k++)
        {
            int firstCorner = order[k] * 3;
            for (int corner = firstCorner; corner < firstCorner + 3; corner++)
            {
                foreach (int other in mesh.Partners(corner))
                {
                    int neighbour = other / 3;
                    if (chartOf[neighbour] == chart && !reached.Contains(neighbour) && mesh.SmoothEdge(corner, other))
                    {
                        reached.Add(neighbour);
                        order.Add(neighbour);
                    }
                }
            }
        }

        return (order, reached);
    }

    /// <summary>
    /// The first place, in the order the chart grew, from which it has to be cut back: the later of two triangles
    /// that overlap once the chart is laid flat. A triangle that folds over flips there, so it overlaps its
    /// neighbour and is cut too. -1 when the chart is fine as it is. Never 0, so a cut chart keeps its seed.
    /// </summary>
    private int FirstBadRank(List<int> members)
    {
        if (members.Count < 2)
        {
            return -1;
        }

        var flat = mesh.Flatten(members);
        double extent = Math.Max(UnwrapMath.Max(flat.X) - UnwrapMath.Min(flat.X), UnwrapMath.Max(flat.Y) - UnwrapMath.Min(flat.Y));
        var overlap = TriangleOverlap.First(flat.X, flat.Y, members.Count, ProjectedOverlap * extent * extent);
        if (overlap.A < 0)
        {
            return -1;
        }

        return Math.Max(1, Math.Max(overlap.A, overlap.B));
    }
}
