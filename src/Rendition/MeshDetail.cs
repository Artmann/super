using System.Numerics;

namespace Rendition;

/// <summary>A simpler version of a part, and how far its surface may stray, in the model's units.</summary>
internal readonly record struct DetailLevel(uint FirstIndex, uint IndexCount, float Error);

/// <summary>The sphere around a part, in the model's units, and its simpler versions, finest first.</summary>
internal readonly record struct PartDetail(Vector3 Center, float Radius, DetailLevel[] Levels);

/// <summary>
/// Makes simpler versions of a mesh that use the same vertices, so each is only another
/// run of indices. Vertices that share a grid cell and face the same way merge into one.
/// </summary>
internal static class MeshDetail
{
    // A level has to save this much over the one before it to be worth keeping.
    private const float MaxShareOfPrevious = 0.7f;

    /// <summary>How many grid cells span each level's longest side.</summary>
    private static ReadOnlySpan<int> Divisions => [64, 32, 16, 8];

    /// <summary>Appends the simpler versions of <paramref name="part"/> to <paramref name="indices"/>.</summary>
    public static PartDetail Build(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> part, List<uint> indices)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (uint index in part)
        {
            min = Vector3.Min(min, vertices[(int)index].Position);
            max = Vector3.Max(max, vertices[(int)index].Position);
        }

        var center = (min + max) / 2f;
        float radius = 0f;
        foreach (uint index in part)
        {
            radius = MathF.Max(radius, Vector3.Distance(center, vertices[(int)index].Position));
        }

        var size = max - min;
        float longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        var levels = new List<DetailLevel>();
        int previous = part.Length;
        foreach (int divisions in Divisions)
        {
            float cell = longest / divisions;
            uint[] simpler = cell > 0f ? Simplify(vertices, part, cell) : [];
            if (simpler.Length == 0 || simpler.Length > previous * MaxShareOfPrevious)
            {
                continue;
            }

            levels.Add(new DetailLevel((uint)indices.Count, (uint)simpler.Length, cell));
            indices.AddRange(simpler);
            previous = simpler.Length;
        }

        return new PartDetail(center, radius, levels.ToArray());
    }

    /// <summary>Merges vertices on a grid. The triangles that still have three corners are what's left.</summary>
    public static uint[] Simplify(ReadOnlySpan<Vertex> vertices, ReadOnlySpan<uint> indices, float cellSize)
    {
        var cells = new Dictionary<(int X, int Y, int Z, int Facing), int>();
        var clusterOf = new Dictionary<uint, int>();
        var sums = new List<Vector3>();
        var counts = new List<int>();
        foreach (uint index in indices)
        {
            if (clusterOf.ContainsKey(index))
            {
                continue;
            }

            ref readonly var vertex = ref vertices[(int)index];
            var cell = vertex.Position / cellSize;
            int facing = (vertex.Normal.X < 0f ? 1 : 0) | (vertex.Normal.Y < 0f ? 2 : 0) | (vertex.Normal.Z < 0f ? 4 : 0);
            var key = ((int)MathF.Floor(cell.X), (int)MathF.Floor(cell.Y), (int)MathF.Floor(cell.Z), facing);
            if (!cells.TryGetValue(key, out int cluster))
            {
                cluster = sums.Count;
                cells.Add(key, cluster);
                sums.Add(Vector3.Zero);
                counts.Add(0);
            }

            clusterOf.Add(index, cluster);
            sums[cluster] += vertex.Position;
            counts[cluster]++;
        }

        // Each cluster becomes its real vertex nearest the middle, which brings its own UVs.
        var kept = new uint[sums.Count];
        var nearest = new float[sums.Count];
        Array.Fill(nearest, float.MaxValue);
        foreach (var (index, cluster) in clusterOf)
        {
            float distance = Vector3.DistanceSquared(vertices[(int)index].Position, sums[cluster] / counts[cluster]);
            if (distance < nearest[cluster] || (distance == nearest[cluster] && index < kept[cluster]))
            {
                nearest[cluster] = distance;
                kept[cluster] = index;
            }
        }

        var simpler = new List<uint>();
        var seen = new HashSet<(int, int, int)>();
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = clusterOf[indices[i]];
            int b = clusterOf[indices[i + 1]];
            int c = clusterOf[indices[i + 2]];
            if (a == b || b == c || a == c)
            {
                continue;
            }

            // Turning the smallest to the front spots repeats and keeps the winding.
            var triangle = a < b && a < c ? (a, b, c) : b < c ? (b, c, a) : (c, a, b);
            if (seen.Add(triangle))
            {
                simpler.Add(kept[a]);
                simpler.Add(kept[b]);
                simpler.Add(kept[c]);
            }
        }

        return simpler.ToArray();
    }
}
