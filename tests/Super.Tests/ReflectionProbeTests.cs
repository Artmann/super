using System.Numerics;
using Rendition;

namespace Super.Tests;

public class ReflectionProbeTests
{
    private static readonly Vector3 BoxMin = new(-3f, -1f, -10f);
    private static readonly Vector3 BoxMax = new(10f, 6f, 7f);

    [Fact]
    public void A_fifth_probe_is_refused()
    {
        var view = new View();
        for (int key = 0; key < 4; key++)
        {
            view.ReflectionProbe(key, new Vector3(key, 1.6f, 0f), BoxMin, BoxMax);
        }

        var error = Assert.Throws<InvalidOperationException>(() => view.ReflectionProbe(4, Vector3.Zero, BoxMin, BoxMax));

        Assert.Contains("4", error.Message);
        Assert.Equal(4, view.ReflectionProbes.Length);
    }

    [Fact]
    public void A_probe_whose_box_excludes_its_position_is_refused()
    {
        var view = new View();

        var error = Assert.Throws<ArgumentException>(() => view.ReflectionProbe(1, new Vector3(0f, 7f, 0f), BoxMin, BoxMax));

        Assert.Equal("position", error.ParamName);
        Assert.Contains("position", error.Message);
        Assert.True(view.ReflectionProbes.IsEmpty);
    }

    [Fact]
    public void The_same_key_twice_in_a_frame_is_refused()
    {
        var view = new View();
        view.ReflectionProbe(7, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax);

        var error = Assert.Throws<ArgumentException>(() => view.ReflectionProbe(7, new Vector3(1f, 1.6f, -1f), BoxMin, BoxMax));

        Assert.Equal("key", error.ParamName);
        Assert.Contains("7", error.Message);
        Assert.Equal(1, view.ReflectionProbes.Length);
    }

    [Fact]
    public void A_probe_is_captured_once_until_it_changes()
    {
        var schedule = new ReflectionProbeSchedule();
        var probe = new ReflectionProbeRequest(1, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax, 0.5f);

        Assert.True(schedule.ShouldCapture(probe, hash: 10));
        Assert.False(schedule.ShouldCapture(probe, hash: 10));
        Assert.True(schedule.ShouldCapture(probe, hash: 11));
        Assert.False(schedule.ShouldCapture(probe, hash: 11));
        Assert.True(schedule.ShouldCapture(probe with { Position = new Vector3(4f, 1.6f, -1f) }, hash: 11));
        Assert.False(schedule.ShouldCapture(probe with { Position = new Vector3(4f, 1.6f, -1f) }, hash: 11));
    }

    [Fact]
    public void Each_key_keeps_its_own_slot_and_a_new_key_takes_the_next()
    {
        var schedule = new ReflectionProbeSchedule();
        var first = new ReflectionProbeRequest(5, new Vector3(1f, 1.6f, 0f), BoxMin, BoxMax, 0.5f);
        var second = new ReflectionProbeRequest(2, new Vector3(2f, 1.6f, 0f), BoxMin, BoxMax, 0.5f);

        schedule.ShouldCapture(first, 1);
        schedule.ShouldCapture(second, 1);
        schedule.ShouldCapture(first with { Blend = 1f }, 1);

        Assert.Equal(0, schedule.SlotOf(5));
        Assert.Equal(1, schedule.SlotOf(2));
        Assert.Equal(-1, schedule.SlotOf(9));
    }

    [Fact]
    public void A_probe_no_longer_asked_for_clears_them_all_so_the_rest_are_captured_again()
    {
        var schedule = new ReflectionProbeSchedule();
        var first = new ReflectionProbeRequest(1, new Vector3(1f, 1.6f, 0f), BoxMin, BoxMax, 0.5f);
        var second = new ReflectionProbeRequest(2, new Vector3(2f, 1.6f, 0f), BoxMin, BoxMax, 0.5f);
        schedule.ShouldCapture(first, 1);
        schedule.ShouldCapture(second, 1);

        Assert.False(schedule.Forget([first, second]));
        Assert.True(schedule.Forget([second]));

        Assert.True(schedule.ShouldCapture(second, 1));
        Assert.Equal(0, schedule.SlotOf(2));
    }

    [Fact]
    public void With_reflections_off_no_probe_is_wanted()
    {
        var view = new View();
        view.ReflectionProbe(1, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax);

        Assert.Equal(1, SceneRenderer.WantedReflectionProbes(view, RenderEffects.Bloom).Length);
        Assert.True(SceneRenderer.WantedReflectionProbes(view, RenderEffects.Reflections | RenderEffects.Bloom).IsEmpty);
    }

    [Fact]
    public void The_probes_last_one_frame()
    {
        var view = new View();
        view.Begin(0f);
        view.ReflectionProbe(1, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax, blend: 0.75f);

        Assert.Equal(
            new ReflectionProbeRequest(1, new Vector3(3f, 1.6f, -1f), BoxMin, BoxMax, 0.75f),
            Assert.Single(view.ReflectionProbes.ToArray()));

        view.Begin(0f);

        Assert.True(view.ReflectionProbes.IsEmpty);
    }
}
