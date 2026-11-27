using System.Numerics;
using Super.Testing;
using Super.Ui;
using static Super.Tests.Ui.FocusTests;
using static Super.Tests.Ui.PointerGame;

namespace Super.Tests.Ui;

/// <summary>How focus shows: the marker that slides from control to control, wrapping menus, and menu rows.</summary>
public sealed class FocusLookTests
{
    private static UiView Menu() => new VStack
    {
        new Button("Continue", () => { }) { Style = ButtonStyle.Menu }.Id("continue"),
        new Button("Options", () => { }) { Style = ButtonStyle.Menu }.Id("options"),
        new Button("Quit", () => { }) { Style = ButtonStyle.Menu }.Id("quit"),
    };

    private static void Settle(HeadlessRun<PointerGame> run)
    {
        for (int i = 0; i < 60 && run.Animating; i++)
        {
            run.Ticks(1);
        }

        Assert.False(run.Animating);
    }

    [Fact]
    public void The_marker_shows_on_the_focused_control_once_a_key_moves_focus()
    {
        using var run = Show(_ => Corner(Menu()));
        Assert.Null(run.FocusMarker);

        Press(run, Key.Tab);
        Settle(run);

        Assert.Equal(run.FrameOf("continue"), run.FocusMarker);
    }

    [Fact]
    public void The_marker_slides_from_the_old_control_to_the_new_one()
    {
        using var run = Show(_ => Corner(Menu()));
        Press(run, Key.Tab);
        Settle(run);
        var from = run.FrameOf("continue");
        var to = run.FrameOf("options");

        run.Input.Press(Key.Down);
        run.Ticks(2);

        Assert.Equal("options", FocusedId(run));
        Assert.True(run.Animating);
        var between = Assert.NotNull(run.FocusMarker);
        Assert.True(between.Y > from.Y && between.Y < to.Y, $"The marker should be on its way from y {from.Y} to {to.Y}, but is at {between.Y}.");

        Settle(run);
        Assert.Equal(to, run.FocusMarker);
    }

    [Fact]
    public void With_animations_off_the_marker_jumps()
    {
        using var run = Show(_ => Corner(Menu()).Animations(false));
        Press(run, Key.Tab);

        Press(run, Key.Down);

        Assert.Equal(run.FrameOf("options"), run.FocusMarker);
        Assert.False(run.Animating);
    }

    [Fact]
    public void A_popup_that_opens_takes_the_marker_where_it_is_without_sliding()
    {
        bool open = false;
        using var run = Show(_ => Corner(new VStack
        {
            new Button("Page", () => { }).Id("page"),
            new Text("").Sheet(open, () => open = false, new Button("Done", () => open = false).DefaultFocus().Id("done")),
        }));
        Press(run, Key.Tab);
        Settle(run);

        open = true;
        for (int i = 0; i < 30 && FocusedId(run) is not "done"; i++)
        {
            run.Ticks(1);
        }

        Assert.Equal("done", FocusedId(run));
        Assert.Equal(run.FrameOf("done"), run.FocusMarker);
    }

    [Fact]
    public void The_mouse_hides_the_marker()
    {
        using var run = Show(_ => Corner(Menu()));
        Press(run, Key.Tab);
        Settle(run);

        var quit = run.FrameOf("quit");
        Click(run, new Vector2(quit.X + quit.Width / 2f, quit.Y + quit.Height / 2f));

        Assert.Null(run.FocusMarker);
    }

    [Fact]
    public void Down_from_the_last_item_stops_there_without_FocusWraps()
    {
        using var run = Show(_ => Corner(Menu()));
        Press(run, Key.Tab);
        Press(run, Key.Up);

        Assert.Equal("continue", FocusedId(run));
    }

    [Theory]
    [InlineData(Key.Up, "quit")]
    [InlineData(Key.Down, "options")]
    public void FocusWraps_comes_round_from_one_end_to_the_other(Key key, string expected)
    {
        using var run = Show(_ => Corner(Menu().FocusWraps()));
        Press(run, Key.Tab);

        Press(run, key);
        Assert.Equal(expected, FocusedId(run));

        Press(run, Key.Down);
        Press(run, Key.Down);
        Assert.Equal(expected == "quit" ? "options" : "continue", FocusedId(run));
    }

    [Fact]
    public void FocusWraps_keeps_the_arrows_inside_even_with_a_control_beyond_it()
    {
        using var run = Show(_ => Corner(new VStack
        {
            Menu().FocusWraps(),
            new Button("Back", () => { }).Id("back"),
        }));
        Press(run, Key.Tab);
        Press(run, Key.Up);
        Assert.Equal("quit", FocusedId(run));

        Press(run, Key.Down);

        Assert.Equal("continue", FocusedId(run));
    }

    [Fact]
    public void Right_comes_round_a_wrapping_row_too()
    {
        using var run = Show(_ => Corner(new HStack
        {
            new Button("A", () => { }).Id("a"),
            new Button("B", () => { }).Id("b"),
        }.FocusWraps()));
        Press(run, Key.Tab);
        Press(run, Key.Right);
        Assert.Equal("b", FocusedId(run));

        Press(run, Key.Right);

        Assert.Equal("a", FocusedId(run));
    }

    [Fact]
    public void A_menu_row_is_as_wide_as_its_menu_with_the_label_at_the_start()
    {
        using var run = Show(_ => Corner(Menu()));
        var row = run.FrameOf("options");
        var label = run.ShownText.Single(text => text.Text == "Options");

        Assert.Equal(300f, row.Width);
        Assert.True(label.Frame.X - row.X < 30f, $"The label should start near the row's start, at {row.X}, but starts at {label.Frame.X}.");
    }
}
