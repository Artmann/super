using System.Globalization;
using Rendition;
using Super.Ui;

namespace Super.Tests;

public class BakeOverlayTests
{
    [Fact]
    public void Overlay_reads_name_and_percent()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            Assert.Equal("Baking house-garden, 42%", BakeOverlay.Text(new BakeProgress("house-garden", 0.424f, 0, 1.5f)));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Overlay_counts_the_others_waiting()
    {
        Assert.Equal("Baking cottage, 7% (3 more)", BakeOverlay.Text(new BakeProgress("cottage", 0.07f, 3, 1.5f)));
    }

    [Fact]
    public void No_overlay_without_progress()
    {
        var game = new Text("Score");

        Assert.Null(BakeOverlay.Text(null));
        Assert.Null(BakeOverlay.Over(null, null));
        var over = Assert.IsType<ZStack>(BakeOverlay.Over(game, null));
        Assert.Equal(new UiView?[] { game, null }, over);
    }

    [Fact]
    public void The_games_view_keeps_its_place_when_the_line_appears()
    {
        var game = new Text("Score");

        var over = Assert.IsType<ZStack>(BakeOverlay.Over(game, new BakeProgress("cottage", 0.5f, 0, 1f)));

        Assert.Equal(2, over.Count());
        Assert.Same(game, over.First());
        Assert.NotNull(over.Last());
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

    [Theory]
    [InlineData(true, new[] { "Score 12", "Baking cottage, 50%" })]
    [InlineData(false, new[] { "Score 12" })]
    public void Play_shows_the_line_over_the_games_interface(bool showsBakes, string[] shown)
    {
        using var host = new GameHost<Scored>(1, 30, null)
        {
            ShowsBakes = showsBakes,
            BakeProgress = new BakeProgress("cottage", 0.5f, 0, 1f),
        };

        host.Draw(1f);

        Assert.Equal(shown, host.Ui.ShownText.Select(text => text.Text));
    }

    [Fact]
    public void Areas_waiting_before_one_starts_are_counted()
    {
        Assert.Equal("Waiting to bake 2 areas", BakeOverlay.Text(new BakeProgress("", 0f, 2, 0f)));
        Assert.Equal("Waiting to bake 1 area", BakeOverlay.Text(new BakeProgress("", 0f, 1, 0f)));
    }
}
