using System.Numerics;
using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>The theme, text styles, control size and turning controls off, which flow down the tree.</summary>
public sealed class EnvironmentTests
{
    private static readonly Color Warm = Color.Hex("#E0896B");

    private static UiTree Show(UiView view)
    {
        var tree = new UiTree();
        tree.Update(view, 800, 600);
        return tree;
    }

    private static ShownText Find(UiView view, string text) => Assert.Single(Show(view).ShownText, shown => shown.Text == text);

    /// <summary>Says which theme it sits in, read from the environment in Body.</summary>
    private sealed record ThemeName : UiView
    {
        public override UiView Body => new Text(Environment.Theme == Theme.Light ? "light" : "dark");
    }

    [Fact]
    public void A_text_style_gives_the_themes_size_and_weight()
    {
        var title = Find(new Text("Settings").TextStyle(TextStyle.Title), "Settings");
        var caption = Find(new Text("v1.2").TextStyle(TextStyle.Caption), "v1.2");

        Assert.Equal(24f, title.Size);
        Assert.Equal(FontWeight.SemiBold, title.Weight);
        Assert.True(title.Bold);
        Assert.Equal(11f, caption.Size);
        Assert.Equal(FontWeight.Medium, caption.Weight);
        Assert.False(caption.Bold);
    }

    [Fact]
    public void A_size_or_weight_further_in_beats_the_text_style_around_it()
    {
        var view = new VStack { new Text("small").FontSize(9f).Weight(FontWeight.Regular) }.TextStyle(TextStyle.LargeTitle);

        var shown = Find(view, "small");

        Assert.Equal(9f, shown.Size);
        Assert.Equal(FontWeight.Regular, shown.Weight);
    }

    [Fact]
    public void A_text_style_further_in_beats_a_size_around_it()
    {
        var shown = Find(new VStack { new Text("head").TextStyle(TextStyle.Headline) }.FontSize(30f), "head");

        Assert.Equal(14f, shown.Size);
        Assert.Equal(FontWeight.SemiBold, shown.Weight);
    }

    [Fact]
    public void A_typography_of_your_own_sizes_the_text_styles()
    {
        var theme = Theme.Dark with { Typography = Typography.Default with { Title = new TypeFace(40f, FontWeight.Bold) } };

        var shown = Find(new Text("Big").TextStyle(TextStyle.Title).Theme(theme), "Big");

        Assert.Equal(40f, shown.Size);
        Assert.Equal(FontWeight.Bold, shown.Weight);
    }

    [Fact]
    public void Italic_text_is_marked_italic()
    {
        Assert.True(Find(new Text("aside").Italic(), "aside").Italic);
        Assert.False(Find(new Text("plain"), "plain").Italic);
    }

    [Fact]
    public void Each_weight_of_the_built_in_font_measures_differently()
    {
        // Heavier strokes take more room, so a weight that silently fell back to another would measure the same.
        float Width(UiView text) => Show(text).ShownText.Single().Frame.Width;
        const string words = "Wandering Merchant";

        float[] widths =
        [
            Width(new Text(words)),
            Width(new Text(words).Weight(FontWeight.Medium)),
            Width(new Text(words).Weight(FontWeight.SemiBold)),
            Width(new Text(words).Weight(FontWeight.Bold)),
        ];

        Assert.Equal(widths.Order(), widths);
        Assert.Equal(4, widths.Distinct().Count());
        Assert.NotEqual(widths[0], Width(new Text(words).Italic()));
    }

    [Fact]
    public void A_theme_colours_plain_text_with_its_foreground()
    {
        Assert.Equal(Theme.Light.Colors.Foreground, Find(new Text("ink").Theme(Theme.Light), "ink").Color);
    }

    [Fact]
    public void A_theme_further_in_recolours_text_that_had_the_outer_themes_foreground()
    {
        var view = new VStack { new Text("inner").Theme(Theme.Light) }.Theme(Theme.Dark);

        Assert.Equal(Theme.Light.Colors.Foreground, Find(view, "inner").Color);
    }

    [Fact]
    public void A_theme_keeps_a_colour_set_outside_it()
    {
        var view = new VStack { new Text("warm").Theme(Theme.Light) }.Foreground(Warm);

        Assert.Equal(Warm, Find(view, "warm").Color);
    }

    [Fact]
    public void A_tone_takes_its_colour_from_the_theme_it_sits_in()
    {
        var view = new VStack { new Text("hint").Foreground(Tone.Muted) }.Theme(Theme.Light);

        Assert.Equal(Theme.Light.Colors.MutedForeground, Find(view, "hint").Color);
        Assert.Equal(Theme.Dark.Colors.Destructive, Find(new Text("danger").Foreground(Tone.Destructive), "danger").Color);
    }

    [Fact]
    public void A_component_runs_again_when_the_theme_around_it_changes()
    {
        var tree = new UiTree();

        tree.Update(new ThemeName().Theme(Theme.Light), 800, 600);
        Assert.Equal("light", tree.ShownText.Single().Text);

        tree.Update(new ThemeName().Theme(Theme.Dark), 800, 600);
        Assert.Equal("dark", tree.ShownText.Single().Text);
    }

    [Fact]
    public void A_control_size_sizes_a_buttons_text_and_height()
    {
        var small = Show(new Button("Go").Id("b").ControlSize(ControlSize.Small));
        var large = Show(new Button("Go").Id("b").ControlSize(ControlSize.Large));

        Assert.Equal(12f, small.ShownText.Single().Size);
        Assert.Equal(16f, large.ShownText.Single().Size);
        Assert.Equal(28f, small.FrameOf("b").Height);
        Assert.Equal(44f, large.FrameOf("b").Height);
    }

    [Fact]
    public void Centred_text_puts_a_short_line_in_the_middle_of_a_long_one()
    {
        float LastLineStart(TextAlignment alignment)
        {
            var tree = Show(new Text("A much longer first line\nab").TextAlign(alignment));
            var glyphs = tree.Quads.ToArray();
            float lastRow = glyphs.Max(quad => quad.Position.Y);
            return glyphs.Where(quad => quad.Position.Y > lastRow - 4f).Min(quad => quad.Position.X);
        }

        float leading = LastLineStart(TextAlignment.Leading);
        float center = LastLineStart(TextAlignment.Center);
        float trailing = LastLineStart(TextAlignment.Trailing);

        Assert.True(center > leading + 40f, $"centred at {center}, leading at {leading}");
        Assert.True(trailing > center + 40f, $"trailing at {trailing}, centred at {center}");
    }

    [Fact]
    public void A_click_inside_disabled_does_nothing()
    {
        using var run = Run(game => Corner(new Text("go").Frame(100f, 100f).Background(Color.Black).OnClick(() => game.Log.Add("go")).Disabled()));

        Click(run, new Vector2(40f, 40f));

        Assert.Empty(run.Game.Log);
    }

    [Fact]
    public void Enabling_again_further_in_doesnt_undo_disabled()
    {
        using var run = Run(game => Corner(new VStack { new Button("go", () => game.Log.Add("go")).Disabled(false) }.Disabled()));

        Click(run, new Vector2(10f, 10f));

        Assert.Empty(run.Game.Log);
    }

    [Fact]
    public void A_button_outside_disabled_still_clicks()
    {
        using var run = Run(game => Corner(new Button("go", () => game.Log.Add("go")).Disabled(false)));

        Click(run, new Vector2(10f, 10f));

        Assert.Equal(["go"], run.Game.Log);
    }

    [Fact]
    public void A_pressable_inside_disabled_shows_its_resting_look()
    {
        using var run = Run(_ => Corner(new Pressable(look => new Text(look.Hovered ? "over" : "rest").Frame(100f, 100f).Background(Color.Black)).Disabled()));

        run.Input.MoveMouse(new Vector2(40f, 40f));
        run.Ticks(2);

        Assert.Equal("rest", run.ShownText.Single().Text);
    }

    private static HeadlessRun<PointerGame> Run(Func<PointerGame, UiView> screen)
    {
        var run = HeadlessGame.Create<PointerGame>(1);
        run.Game.Screen = screen;
        run.Ticks(1);
        return run;
    }

    private static UiView Corner(UiView view) =>
        view.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    private static void Click(HeadlessRun<PointerGame> run, Vector2 at)
    {
        run.Input.MoveMouse(at);
        run.Ticks(1);
        run.Input.Hold(MouseButton.Left);
        run.Ticks(1);
        run.Input.Release(MouseButton.Left);
        run.Ticks(2);
    }
}
