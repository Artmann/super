using System.Globalization;
using Rendition;
using Super.Ui;

namespace Super.Tests;

public class StatsOverlayTests
{
    private static readonly RenderStats Measured = new(
        GpuMilliseconds: 6.98,
        WaitMilliseconds: 4.5,
        SlowestPasses: [new GpuPassTime("Shadows", 2.62), new GpuPassTime("G-buffer", 2.571)],
        DrawCalls: 1234,
        CameraTriangles: 5_663_912,
        ShadowTriangles: 42_120,
        ShadowTilesDrawn: 25,
        ShadowTilesKept: 71,
        GpuMemoryUsedBytes: 812L * 1024 * 1024,
        GpuMemoryReservedBytes: 1088L * 1024 * 1024,
        Width: 1920,
        Height: 1080);

    [Fact]
    public void Lists_the_renderers_figures_the_same_way_in_every_language()
    {
        var stats = new FrameStats(76.4, 13.09, 1792, 34L * 1024 * 1024 + 512 * 1024, 12, Measured);
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            Assert.Equal(
                [
                    new("Frame", "76 fps, 13.1 ms"),
                    new("CPU", "8.6 ms"),
                    new("GPU", "7.0 ms"),
                    new("  Shadows", "2.62 ms"),
                    new("  G-buffer", "2.57 ms"),
                    new("Draw calls", "1,234"),
                    new("Triangles", "5.66 M"),
                    new("  Shadows", "42.1 k"),
                    new("Shadow tiles", "25 drawn, 71 kept"),
                    new("GPU memory", "812 of 1,088 MB"),
                    new("Heap", "34.5 MB, 1,792 B a frame"),
                    new("Collections", "12"),
                    new("1920x1080", "Test GPU"),
                ],
                StatsOverlay.Rows(stats, "Test GPU"));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Without_the_renderer_measured_only_the_frame_and_the_heap_show()
    {
        var stats = new FrameStats(60.0, 16.7, 0, 1024 * 1024, 3);

        Assert.Equal(
            ["Frame", "Heap", "Collections"],
            StatsOverlay.Rows(stats, "Test GPU").Select(row => row.Label));
    }

    [Fact]
    public void A_gpu_that_cant_time_its_passes_shows_no_gpu_time_and_no_tiles_without_lamps()
    {
        var stats = new FrameStats(60.0, 16.7, 0, 0, 0, Measured with
        {
            GpuMilliseconds = 0.0,
            SlowestPasses = [],
            ShadowTilesDrawn = 0,
            ShadowTilesKept = 0,
        });

        var labels = StatsOverlay.Rows(stats, "Test GPU").Select(row => row.Label).ToArray();

        Assert.Contains("CPU", labels);
        Assert.Contains("Draw calls", labels);
        Assert.DoesNotContain("GPU", labels);
        Assert.DoesNotContain("Shadow tiles", labels);
    }

    [Theory]
    [InlineData(812L, "812")]
    [InlineData(9_999L, "9,999")]
    [InlineData(12_432L, "12.4 k")]
    [InlineData(12_432_284L, "12.43 M")]
    public void Counts_triangles_in_thousands_and_millions(long count, string text)
    {
        Assert.Equal(text, StatsOverlay.Triangles(count));
    }

    [Theory]
    [InlineData(false, new[] { "Score 12" })]
    [InlineData(true, new[] { "Score 12", "Frame" })]
    public void The_panel_shows_over_the_games_interface_only_when_shown(bool shown, string[] first)
    {
        using var host = new GameHost<Scored>(1, 30, null);
        host.Stats.Shown = shown;

        host.Draw(1f);

        Assert.Equal(first, host.Ui.ShownText.Select(text => text.Text).Take(2));
    }

    private sealed class Scored : IGame
    {
        public void Update(GameTime time)
        {
        }

        public void Draw(View view)
        {
        }

        public UiView? Ui => new Text("Score 12");
    }
}
