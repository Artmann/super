using System.Numerics;
using Rendition;
using Xunit.Abstractions;

namespace Super.Tests;

/// <summary>Picking what each of a frame's many views draws stays cheap.</summary>
[Collection(TimingCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class DrawSelectBenchmarkTests(ITestOutputHelper output)
{
    private const int Views = 24;

    [Fact]
    public void Picking_for_the_tiles_of_four_lights_costs_little_more_than_measuring_every_draw_for_each_tile()
    {
        var draws = DrawBatchBenchmarkTests.CrowdedView(seed: 7);
        for (int i = 0; i < draws.Length; i++)
        {
            draws[i].Detail = new DetailHandle(1);
        }

        List<PartDetail> details = [default, new PartDetail(Vector3.Zero, 0.5f, [new DetailLevel(0, 300, 0.01f), new DetailLevel(300, 90, 0.05f)])];
        var views = TileViews();
        var selected = new DrawItem[draws.Length * 2];
        var fades = new FadeRules(new Vector3(50f, 5f, 50f), smooth: true, _ => null);
        var wind = new WindReach(0.5f, _ => 0f);
        var measured = new DrawBalls();
        var remeasured = new DrawBalls();
        int kept = 0;

        var (select, remeasure) = Timing.Medians(() =>
        {
            kept = 0;
            measured.Measure(draws, details, wind, fades);
            foreach (var view in views)
            {
                kept += DrawSelection.Select(view, draws, measured, selected);
            }
        }, () =>
        {
            for (int i = 0; i < Views; i++)
            {
                remeasured.Measure(draws, details, wind, fades);
            }
        }, warmUp: 100);

        output.WriteLine($"Select: {select:F3} ms, measuring for each view: {remeasure:F3} ms, for {draws.Length} draws in {Views} views, {kept} kept.");

        // Picking is about 0.65 times the baseline, and measuring again for every view, as it once did, about 1.7. The baseline
        // is compute like the picking, not memory like a copy, so the two keep their ratio from a desktop to a CI runner.
        Assert.True(select <= 1.2 * remeasure, $"Picking took {select:F3} ms against {remeasure:F3} ms measuring every draw for each view.");
    }

    // The six faces of four point lights standing in the crowd.
    private static DetailView[] TileViews()
    {
        Vector3[] ways = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        var views = new List<DetailView>();
        for (int light = 0; light < Views / ways.Length; light++)
        {
            var eye = new Vector3(20f + light * 20f, 5f, 30f + light * 13f);
            foreach (var way in ways)
            {
                var up = MathF.Abs(way.Y) > 0.5f ? Vector3.UnitZ : Vector3.UnitY;
                var clip = Matrix4x4.CreateLookAt(eye, eye + way, up) * Projection.PerspectiveInfiniteReversedZ(MathF.PI / 2f, 1f, 0.1f);
                views.Add(new DetailView(clip, eye, 500f, 0f, 2f));
            }
        }

        return views.ToArray();
    }
}
