using System.Numerics;
using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>The motion controls add for feel: presses that squash, numbers that count, losses that trail and views that leave.</summary>
public sealed class MotionTests
{
    private static readonly Color Red = new(1f, 0f, 0f);

    private sealed class Screen
    {
        public UiView View { get; set; } = new Text("Nothing");
    }

    private static HeadlessRun<PointerGame> Run(Screen screen) =>
        PointerGame.Show(_ => screen.View.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading));

    private static ShownText Shown(HeadlessRun<PointerGame> run, string text) => run.ShownText.Single(shown => shown.Text == text);

    private static Vector2 Middle(UiRect frame) => new(frame.X + frame.Width / 2f, frame.Y + frame.Height / 2f);

    /// <summary>Holds the left button down on this text, and waits for the squash to land.</summary>
    private static void HoldOn(HeadlessRun<PointerGame> run, ShownText text)
    {
        run.Input.MoveMouse(Middle(text.Frame));
        run.Ticks(1);
        run.Input.Hold(MouseButton.Left);
        run.Ticks(4);
    }

    [Fact]
    public void A_held_button_is_drawn_smaller_round_its_middle_and_springs_back_when_let_go()
    {
        using var run = Run(new Screen { View = new Button("Play").Padding(20f) });
        var before = Shown(run, "Play");

        HoldOn(run, before);
        var held = Shown(run, "Play");

        Assert.True(held.Size < before.Size, $"The label was {before.Size} and is {held.Size} while held.");
        Assert.True(held.Frame.Width < before.Frame.Width);
        Assert.Equal(Middle(before.Frame).X, Middle(held.Frame).X, 0.01f);

        run.Input.Release(MouseButton.Left);
        run.Ticks(15);
        Assert.Equal(before.Size, Shown(run, "Play").Size, 0.001f);
        Assert.Equal(before.Frame, Shown(run, "Play").Frame);
    }

    [Fact]
    public void A_button_in_a_theme_without_motion_does_not_squash()
    {
        var plain = Theme.Dark with { Motion = ThemeMotion.None };
        using var run = Run(new Screen { View = new Button("Play").Padding(20f).Theme(plain) });
        var before = Shown(run, "Play");

        HoldOn(run, before);

        Assert.Equal(before.Size, Shown(run, "Play").Size);
    }

    [Fact]
    public void Scale_draws_boxes_and_text_smaller_round_the_middle_without_moving_the_layout()
    {
        var tree = new UiTree();
        tree.Update(new VStack(Spacing: 0f)
        {
            new ZStack { new Rectangle().Fill(Red), new Text("Hi") }.Frame(width: 100f, height: 100f).Scale(0.5f),
            new Text("Below"),
        }.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading), 800, 600);
        var plain = new UiTree();
        plain.Update(new Text("Hi"), 800, 600);

        var box = Assert.Single(tree.Quads.ToArray(), quad => quad.Color == new Vector4(1f, 0f, 0f, 1f));
        Assert.Equal(new Vector2(25f, 25f), box.Position);
        Assert.Equal(new Vector2(50f, 50f), box.Size);
        Assert.Equal(plain.ShownText.Single().Size / 2f, tree.ShownText.Single(shown => shown.Text == "Hi").Size, 0.001f);
        Assert.Equal(100f, tree.ShownText.Single(shown => shown.Text == "Below").Frame.Y, 0.01f);
    }

    [Fact]
    public void Counting_text_counts_through_the_numbers_between_to_the_new_value()
    {
        var screen = new Screen { View = new CountingText(0f) };
        using var run = Run(screen);
        Assert.Equal("0", run.ShownText.Single().Text);

        screen.View = new CountingText(100f);
        run.Ticks(4);
        int partway = int.Parse(run.ShownText.Single().Text, System.Globalization.CultureInfo.InvariantCulture);
        run.Ticks(20);

        Assert.InRange(partway, 1, 99);
        Assert.Equal("100", run.ShownText.Single().Text);
    }

    [Fact]
    public void Counting_text_reads_the_way_its_format_says()
    {
        using var run = Run(new Screen { View = new CountingText(250f) { Format = n => $"{n:0} gold" } });

        Assert.Equal("250 gold", run.ShownText.Single().Text);
    }

    /// <summary>The pale quads a linear gauge draws for what it lost, whose foreground is red so they can be told apart.</summary>
    private static OverlayQuadPiece[] Trails(UiTree tree) =>
        [.. tree.Quads.ToArray().Where(quad => quad.Color.X > 0f && quad.Color.Y == 0f && quad.Color.W is > 0.4f and < 0.5f).Select(quad => new OverlayQuadPiece(quad.Position.X, quad.Size.X))];

    private readonly record struct OverlayQuadPiece(float X, float Width);

    private static UiView Health(float value, ThemeMotion motion) =>
        new Gauge("Health", value) { Style = MeterStyle.Linear }
            .Frame(width: 200f)
            .Theme(Theme.Dark with { Colors = Theme.Dark.Colors with { Foreground = Red }, Motion = motion });

    [Fact]
    public void A_gauge_that_drops_shows_what_it_lost_draining_away_after_it()
    {
        var tree = new UiTree();
        tree.Update(Health(1f, ThemeMotion.Default), 800, 600);
        Assert.Empty(Trails(tree));

        tree.Update(Health(0.5f, ThemeMotion.Default), 800, 600);
        var lost = Assert.Single(Trails(tree));
        for (int i = 0; i < 20; i++)
        {
            tree.Update(Health(0.5f, ThemeMotion.Default), 800, 600);
        }

        var draining = Assert.Single(Trails(tree));
        for (int i = 0; i < 40; i++)
        {
            tree.Update(Health(0.5f, ThemeMotion.Default), 800, 600);
        }

        // It reaches from the fill's start to where the bar was, and shrinks to nothing behind the fill.
        Assert.Equal(200f, lost.Width, 0.5f);
        Assert.Equal(lost.X, draining.X);
        Assert.True(draining.Width < lost.Width && draining.Width > 100f, $"It was {lost.Width} wide and is {draining.Width}.");
        Assert.Empty(Trails(tree));
    }

    [Fact]
    public void A_gauge_in_a_theme_without_trails_drops_at_once()
    {
        var tree = new UiTree();
        tree.Update(Health(1f, ThemeMotion.None), 800, 600);

        tree.Update(Health(0.5f, ThemeMotion.None), 800, 600);

        Assert.Empty(Trails(tree));
    }

    [Fact]
    public void A_presence_eases_in_and_when_hidden_fades_out_before_it_goes()
    {
        var screen = new Screen { View = new Presence(false, new Text("Paused")) };
        using var run = Run(screen);
        Assert.Empty(run.ShownText);

        screen.View = new Presence(true, new Text("Paused"));
        run.Ticks(2);
        float arriving = Shown(run, "Paused").Color.A;
        run.Ticks(10);
        Assert.Equal(1f, Shown(run, "Paused").Color.A);

        screen.View = new Presence(false, new Text("Paused"));
        run.Ticks(2);
        float leaving = Shown(run, "Paused").Color.A;
        run.Ticks(10);

        Assert.InRange(arriving, 0.01f, 0.99f);
        Assert.InRange(leaving, 0.01f, 0.99f);
        Assert.Empty(run.ShownText);
        Assert.False(run.Animating);
    }

    [Fact]
    public void A_presence_on_its_way_out_lets_the_mouse_through_to_what_is_under_it()
    {
        var log = new List<string>();
        var screen = new Screen();
        UiView Over(bool shown) => new ZStack
        {
            new Button("Below", () => log.Add("Below")).Frame(width: 200f, height: 60f),
            new Presence(shown, new Button("Cover", () => log.Add("Cover")).Frame(width: 200f, height: 60f)) { Seconds = 2f },
        };
        screen.View = Over(true);
        using var run = Run(screen);
        run.Ticks(70);
        var middle = Middle(Shown(run, "Cover").Frame);
        PointerGame.Click(run, middle);

        screen.View = Over(false);
        run.Ticks(1);
        PointerGame.Click(run, middle);

        Assert.Contains("Cover", run.ShownText.Select(shown => shown.Text));
        Assert.Equal(["Cover", "Below"], log);
    }
}
