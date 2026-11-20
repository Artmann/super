using System.Numerics;
using Rendition;
using Rendition.Passes;
using Xunit.Abstractions;

namespace Super.Tests;

/// <summary>Grouping a crowded frame's draws into instanced draws stays cheap.</summary>
[Collection(TimingCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class DrawBatchBenchmarkTests(ITestOutputHelper output)
{
    [Fact]
    public void Batching_a_crowded_view_is_much_faster_than_sorting_its_draws_by_comparing_them()
    {
        var draws = CrowdedView(seed: 7);
        int[] order = new int[draws.Length];
        int[] compared = new int[draws.Length];
        var batches = new List<DrawBatch>();

        var (batch, compare) = Timing.Medians(() =>
        {
            Reset(order);
            SceneDraws.Batch(draws, order, keepOrder: false, batches);
        }, () =>
        {
            Reset(compared);
            Array.Sort(compared, (a, b) => Compare(draws[a], a, draws[b], b));
        }, warmUp: 200);

        output.WriteLine($"Batch: {batch:F3} ms, comparing: {compare:F3} ms, for {draws.Length} draws.");
        Assert.True(batch <= 0.5 * compare, $"Batching took {batch:F3} ms against {compare:F3} ms sorting by comparing.");
    }

    private static void Reset(int[] order)
    {
        for (int i = 0; i < order.Length; i++)
        {
            order[i] = i;
        }
    }

    // The plainest way to bring the same looks together: by mesh, part, size and material, then the order they came in.
    private static int Compare(in DrawItem a, int placeA, in DrawItem b, int placeB)
    {
        int byMesh = a.Mesh.Index.CompareTo(b.Mesh.Index);
        if (byMesh != 0)
        {
            return byMesh;
        }

        int byPart = a.FirstIndex.CompareTo(b.FirstIndex);
        if (byPart != 0)
        {
            return byPart;
        }

        int bySize = a.IndexCount.CompareTo(b.IndexCount);
        if (bySize != 0)
        {
            return bySize;
        }

        int byMaterial = a.Material.Index.CompareTo(b.Material.Index);
        return byMaterial != 0 ? byMaterial : placeA.CompareTo(placeB);
    }

    /// <summary>
    /// A view like the Reef's: a shoal of six thousand copies of one model at three levels of detail, a few hundred of
    /// another, and some twelve hundred copies of sixty kinds of scenery, all mixed together.
    /// </summary>
    internal static DrawItem[] CrowdedView(int seed)
    {
        var random = new System.Random(seed);
        var draws = new List<DrawItem>();
        for (int i = 0; i < 6000; i++)
        {
            int level = random.Next(3);
            draws.Add(Draw(mesh: 1, firstIndex: (uint)(level * 5000), indexCount: (uint)(4968 >> level), material: 1, random));
        }

        for (int i = 0; i < 240; i++)
        {
            draws.Add(Draw(mesh: 2, firstIndex: 0, indexCount: 17967, material: 2, random));
        }

        for (int i = 0; i < 1250; i++)
        {
            int kind = random.Next(60);
            draws.Add(Draw(mesh: 3 + kind, firstIndex: 0, indexCount: (uint)(600 + kind * 30), material: 3 + kind % 7, random));
        }

        var mixed = draws.ToArray();
        random.Shuffle(mixed);
        return mixed;
    }

    private static DrawItem Draw(int mesh, uint firstIndex, uint indexCount, int material, System.Random random) => new()
    {
        Mesh = new MeshHandle(mesh),
        FirstIndex = firstIndex,
        IndexCount = indexCount,
        Material = new MaterialHandle(material),
        Transform = Matrix4x4.CreateTranslation(random.NextSingle() * 100f, random.NextSingle() * 10f, random.NextSingle() * 100f),
    };
}
