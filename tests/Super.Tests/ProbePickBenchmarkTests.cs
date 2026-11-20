using System.Numerics;
using Rendition;
using Xunit.Abstractions;

namespace Super.Tests;

/// <summary>Picking a frame's probes stays cheap while most of them still wait for their first capture.</summary>
[Collection(TimingCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class ProbePickBenchmarkTests(ITestOutputHelper output)
{
    private const int Side = 32;
    private const int Height = 16;

    [Fact]
    public void Picking_among_every_probe_of_a_new_scene_costs_little_more_than_measuring_how_far_each_is()
    {
        // As a scene starts at high quality: four cascades of 32 by 16 by 32 probes, none captured yet.
        var states = new ProbeStates();
        var positions = new List<Vector3>();
        for (int group = 0; group < ProbeCascades.Count; group++)
        {
            float spacing = 0.5f * (1 << group);
            states.ReplaceGroup(group, Side * Height * Side);
            for (int slot = 0; slot < Side * Height * Side; slot++)
            {
                var position = new Vector3(slot % Side - Side / 2, slot / Side % Height - Height / 2, slot / (Side * Height) - Side / 2) * spacing;
                states.MakeNew(group, slot, position);
                positions.Add(position);
            }
        }

        var picker = new ProbeBatchPicker(states);
        var camera = new Vector3(0.3f, 1.5f, -0.2f);
        var forward = new Vector3(0f, 0f, -1f);
        Vector3[] all = [.. positions];
        float nearest = 0f;

        var (pick, measure) = Timing.Medians(() => picker.Pick(1, 1024, camera, forward, int.MaxValue), () =>
        {
            float least = float.MaxValue;
            foreach (var position in all)
            {
                least = MathF.Min(least, Vector3.DistanceSquared(position, camera));
            }

            nearest = least;
        }, warmUp: 200);

        output.WriteLine($"Pick: {pick:F3} ms, measuring: {measure:F3} ms, for {all.Length} probes, the nearest {MathF.Sqrt(nearest):F2} away.");
        // Picking is about 3.5 times the measuring on a desktop and up to 9 on CI runners, whose plain loop is quicker.
        // Sorting every probe instead of bringing the lowest forward is about 20 times, so 12 still catches that.
        Assert.True(pick <= 12 * measure,$"Picking took {pick:F3} ms against {measure:F3} ms measuring each probe's distance.");
    }
}
