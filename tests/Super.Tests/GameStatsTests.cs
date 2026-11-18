using Microsoft.Extensions.Time.Testing;
using Rendition;

namespace Super.Tests;

public class GameStatsTests
{
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void Averages_the_gpu_over_the_timed_frames_and_the_wait_over_every_frame()
    {
        var stats = new GameStats(_time);

        stats.AddRender([new GpuPassTime("Shadows", 2.0), new GpuPassTime("Sky", 1.0)], 4.0, Counts(drawCalls: 10));
        stats.AddRender([new GpuPassTime("Shadows", 4.0), new GpuPassTime("Sky", 3.0)], 6.0, Counts(drawCalls: 20));
        stats.AddRender([], 2.0, Counts(drawCalls: 30));
        _time.Advance(TimeSpan.FromSeconds(0.5));
        stats.EndFrame();

        var render = stats.Latest.Render;
        Assert.Equal(5.0, render.GpuMilliseconds, 9);
        Assert.Equal(4.0, render.WaitMilliseconds, 9);
        Assert.Equal([new GpuPassTime("Shadows", 3.0), new GpuPassTime("Sky", 2.0)], render.SlowestPasses);
        Assert.Equal(30, render.DrawCalls);
    }

    [Fact]
    public void Lists_only_the_slowest_passes_slowest_first()
    {
        var stats = new GameStats(_time);
        GpuPassTime[] passes =
        [
            new("A", 1.0), new("B", 7.0), new("C", 3.0), new("D", 6.0), new("E", 2.0), new("F", 5.0), new("G", 4.0),
        ];

        stats.AddRender(passes, 0.0, Counts(drawCalls: 1));
        _time.Advance(TimeSpan.FromSeconds(0.5));
        stats.EndFrame();

        Assert.Equal(["B", "D", "F", "G", "C"], stats.Latest.Render.SlowestPasses!.Select(pass => pass.Name));
    }

    [Fact]
    public void Each_update_measures_only_the_frames_since_the_one_before()
    {
        var stats = new GameStats(_time);
        stats.AddRender([new GpuPassTime("Shadows", 9.0)], 8.0, Counts(drawCalls: 1));
        _time.Advance(TimeSpan.FromSeconds(0.5));
        stats.EndFrame();

        stats.AddRender([new GpuPassTime("Sky", 1.0)], 2.0, Counts(drawCalls: 2));
        _time.Advance(TimeSpan.FromSeconds(0.5));
        stats.EndFrame();

        var render = stats.Latest.Render;
        Assert.Equal(1.0, render.GpuMilliseconds, 9);
        Assert.Equal(2.0, render.WaitMilliseconds, 9);
        Assert.Equal([new GpuPassTime("Sky", 1.0)], render.SlowestPasses);
    }

    [Fact]
    public void A_restart_forgets_the_frames_before_it()
    {
        var stats = new GameStats(_time);
        stats.AddRender([new GpuPassTime("Shadows", 9.0)], 8.0, Counts(drawCalls: 1));
        _time.Advance(TimeSpan.FromSeconds(0.5));
        stats.EndFrame();
        stats.AddRender([new GpuPassTime("Shadows", 9.0)], 8.0, Counts(drawCalls: 1));
        stats.EndFrame();

        stats.Restart();
        Assert.Equal(default, stats.Latest);

        stats.AddRender([new GpuPassTime("Sky", 1.0)], 2.0, Counts(drawCalls: 2));
        _time.Advance(TimeSpan.FromSeconds(0.25));
        stats.EndFrame();
        _time.Advance(TimeSpan.FromSeconds(0.25));
        stats.EndFrame();

        Assert.Equal(4.0, stats.Latest.FramesPerSecond, 9);
        Assert.Equal(1.0, stats.Latest.Render.GpuMilliseconds, 9);
        Assert.Equal([new GpuPassTime("Sky", 1.0)], stats.Latest.Render.SlowestPasses);
    }

    [Fact]
    public void Without_rendered_frames_the_renderer_was_not_measured()
    {
        var stats = new GameStats(_time);
        stats.AddRender([new GpuPassTime("Sky", 1.0)], 2.0, Counts(drawCalls: 2));
        _time.Advance(TimeSpan.FromSeconds(0.5));
        stats.EndFrame();

        _time.Advance(TimeSpan.FromSeconds(0.5));
        stats.EndFrame();

        Assert.False(stats.Latest.Render.Measured);
        Assert.Equal(default, stats.Latest.Render);
    }

    [Fact]
    public void Busy_time_is_the_frame_less_the_wait()
    {
        var render = new RenderStats(0.0, 4.0, [], 0, 0, 0, 0, 0, 0, 0, 0, 0);

        Assert.Equal(6.0, new FrameStats(100.0, 10.0, 0, 0, 0, render).BusyMilliseconds, 9);
        Assert.Equal(10.0, new FrameStats(100.0, 10.0, 0, 0, 0).BusyMilliseconds, 9);
    }

    [Fact]
    public void F3_shows_and_hides_the_panel_on_press_and_never_reaches_the_game()
    {
        var stats = new GameStats(_time);

        Assert.True(stats.TakeKey(Key.F3, pressed: true));
        Assert.True(stats.Shown);
        Assert.True(stats.TakeKey(Key.F3, pressed: false));
        Assert.True(stats.Shown);
        Assert.True(stats.TakeKey(Key.F3, pressed: true));
        Assert.False(stats.Shown);
    }

    [Fact]
    public void Another_toggle_key_takes_over_and_f3_goes_to_the_game()
    {
        var stats = new GameStats(_time) { ToggleKey = Key.F10 };

        Assert.False(stats.TakeKey(Key.F3, pressed: true));
        Assert.False(stats.Shown);
        Assert.True(stats.TakeKey(Key.F10, pressed: true));
        Assert.True(stats.Shown);
    }

    [Fact]
    public void Without_a_toggle_key_every_key_goes_to_the_game()
    {
        var stats = new GameStats(_time) { ToggleKey = null };

        Assert.False(stats.TakeKey(Key.F3, pressed: true));
        Assert.False(stats.TakeKey(Key.A, pressed: true));
        Assert.False(stats.Shown);
    }

    private static RenderStats Counts(int drawCalls) => new(0.0, 0.0, null, drawCalls, 0, 0, 0, 0, 0, 0, 1280, 720);
}
