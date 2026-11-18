using System.Numerics;
using Rendition;

namespace Super.Tests;

public class ProbeLightTrackerTests
{
    private static readonly Vector3 Forward = new(0f, 0f, -1f);
    private static readonly Vector3 Lamp = new(101f, 1f, 101f);

    [Fact]
    public void A_flame_that_flickers_by_a_tenth_leaves_the_probes_round_it_captured()
    {
        var (scheduler, tracker) = Settled(new Vector3(10f, 6f, 2f));

        // A tenth up, then a tenth down, then a little up again: every frame differs from the one before.
        foreach (float flicker in new[] { 1.1f, 0.9f, 1.05f, 0.92f })
        {
            tracker.Follow([LampLight(new Vector3(10f, 6f, 2f) * flicker)], scheduler);
            Assert.True(scheduler.Settled, $"a flicker of {flicker} marked probes");
        }
    }

    [Fact]
    public void A_light_dimmed_to_half_marks_the_probes_round_it()
    {
        var (scheduler, tracker) = Settled(new Vector3(10f, 6f, 2f));

        tracker.Follow([LampLight(new Vector3(5f, 3f, 1f))], scheduler);

        Assert.False(scheduler.Settled);
        Assert.Equal(new ProbeId(ProbeScheduler.FixedGrid, 13), scheduler.Next(8, Vector3.Zero, Forward)[0]);
    }

    [Fact]
    public void A_slow_fade_marks_the_probes_once_it_drifts_past_a_third_of_where_they_were_marked()
    {
        var (scheduler, tracker) = Settled(new Vector3(9f));

        // Each step is a tenth of the last, under the drift on its own, but by 0.6 the light is 40% down on the 9 the
        // probes were marked for.
        foreach (float brightness in new[] { 8.1f, 7.3f, 6.6f })
        {
            tracker.Follow([LampLight(new Vector3(brightness))], scheduler);
            Assert.True(scheduler.Settled, $"{brightness} marked probes");
        }

        tracker.Follow([LampLight(new Vector3(5.4f))], scheduler);
        Assert.False(scheduler.Settled);
    }

    [Fact]
    public void A_light_that_changes_hue_at_the_same_brightness_marks_the_probes()
    {
        var (scheduler, tracker) = Settled(new Vector3(8f, 2f, 2f));

        tracker.Follow([LampLight(new Vector3(2f, 2f, 8f))], scheduler);

        Assert.False(scheduler.Settled);
    }

    [Fact]
    public void A_light_that_comes_on_from_dark_marks_the_probes()
    {
        var (scheduler, tracker) = Settled(Vector3.Zero);

        tracker.Follow([LampLight(new Vector3(0.05f))], scheduler);

        Assert.False(scheduler.Settled);
    }

    // A scheduler whose probes are all captured with the lamp at this colour, and the tracker that saw it.
    private static (ProbeScheduler Scheduler, ProbeLightTracker Tracker) Settled(Vector3 radiance)
    {
        var cascades = new ProbeCascades(ProbeQuality.Low);
        var scheduler = new ProbeScheduler(cascades, 27);
        scheduler.SetFixedGrid(new Vector3(100f, 0f, 100f), 3, 3, 3, 1f);
        scheduler.Scrolled(cascades.Move(Vector3.Zero));
        var tracker = new ProbeLightTracker();
        tracker.Follow([LampLight(radiance)], scheduler);

        for (int guard = 0; guard < 1000 && !scheduler.Settled; guard++)
        {
            scheduler.Updated(scheduler.Next(1024, Vector3.Zero, Forward));
        }

        Assert.True(scheduler.Settled);
        return (scheduler, tracker);
    }

    // A lamp by the fixed grid's middle probe, reaching only it.
    private static LocalLight LampLight(Vector3 radiance) => new()
    {
        Key = 1,
        Position = Lamp,
        Range = 0.5f,
        Radiance = radiance,
    };
}
