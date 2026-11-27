using Super.Testing;

namespace Super.Tests;

/// <summary>Whether the window has focus, as a game reads it to pause while the player is elsewhere.</summary>
public class FocusTests
{
    /// <summary>A game that pauses while it has no focus, and writes down what each tick saw.</summary>
    private class Pauser(Input input) : IGame
    {
        public List<string> Seen { get; } = [];

        public int Played { get; private set; }

        public void Update(GameTime time)
        {
            if (input.Window.FocusLost)
            {
                Seen.Add("lost");
            }

            if (input.Window.FocusGained)
            {
                Seen.Add("gained");
            }

            if (input.Window.HasFocus)
            {
                Played++;
            }
        }
    }

    private class ReadsFocusInDraw(Input input) : IGame
    {
        public void Update(GameTime time)
        {
        }

        public void Draw(View view) => _ = input.Window.HasFocus;
    }

    [Fact]
    public void A_run_with_no_window_has_focus_and_never_changes_it()
    {
        using var run = HeadlessGame.Create<Pauser>(1);

        run.Ticks(3);

        Assert.Equal(3, run.Game.Played);
        Assert.Empty(run.Game.Seen);
    }

    [Fact]
    public void Losing_focus_is_seen_once_and_lasts_until_it_comes_back()
    {
        using var run = HeadlessGame.Create<Pauser>(1);
        run.Ticks(1);

        run.Input.LoseFocus();
        run.Ticks(3);
        run.Input.GainFocus();
        run.Ticks(2);

        Assert.Equal(["lost", "gained"], run.Game.Seen);
        Assert.Equal(3, run.Game.Played);
    }

    [Fact]
    public void Focus_that_goes_and_comes_back_between_ticks_still_shows_both()
    {
        using var run = HeadlessGame.Create<Pauser>(1);

        run.Input.LoseFocus();
        run.Input.GainFocus();
        run.Ticks(2);

        Assert.Equal(["lost", "gained"], run.Game.Seen);
        Assert.Equal(2, run.Game.Played);
    }

    [Fact]
    public void Losing_focus_twice_counts_once()
    {
        using var run = HeadlessGame.Create<Pauser>(1);

        run.Input.LoseFocus();
        run.Ticks(1);
        run.Input.LoseFocus();
        run.Ticks(1);

        Assert.Equal(["lost"], run.Game.Seen);
    }

    [Fact]
    public void Focus_can_only_be_read_in_update()
    {
        var error = Assert.Throws<InvalidOperationException>(() => HeadlessGame.Create<ReadsFocusInDraw>(1));

        Assert.Equal("Input can only be read in Update. Draw and Ui describe state, so they can't look at input.", error.Message);
    }

    [Theory]
    [InlineData(true, 0, 30, 0)]
    [InlineData(false, 0, -1, 0)]
    [InlineData(false, 0, 10, 10)]
    [InlineData(false, 144, -1, 144)]
    [InlineData(false, 144, 10, 10)]
    [InlineData(false, 5, 10, 5)]
    [InlineData(true, 144, 10, 144)]
    [InlineData(true, 144, 0, 144)]
    public void The_unfocused_limit_only_slows_frames_while_the_window_has_no_focus(bool focused, int limit, int unfocused, int expected)
    {
        var screen = new GraphicsScreen { MaxFramesPerSecond = limit, UnfocusedMaxFramesPerSecond = unfocused };

        Assert.Equal(expected, screen.FrameLimit(focused, 30));
        Assert.True(screen.Draws(focused));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(144)]
    public void An_unfocused_limit_of_zero_draws_nothing_and_wakes_once_a_tick(int limit)
    {
        var screen = new GraphicsScreen { MaxFramesPerSecond = limit, UnfocusedMaxFramesPerSecond = 0 };

        Assert.False(screen.Draws(false));
        Assert.Equal(30, screen.FrameLimit(false, 30));
    }

    [Fact]
    public void The_unfocused_limit_keeps_to_the_frame_limit_by_default()
    {
        var screen = new GraphicsScreen { MaxFramesPerSecond = 144 };

        Assert.Equal(-1, screen.UnfocusedMaxFramesPerSecond);
        Assert.Equal(144, screen.FrameLimit(false, 30));
    }

    [Fact]
    public void An_unfocused_limit_below_minus_one_is_refused()
    {
        var screen = new GraphicsScreen();

        var error = Assert.Throws<ArgumentOutOfRangeException>(() => screen.UnfocusedMaxFramesPerSecond = -2);

        Assert.StartsWith("The unfocused frame limit can be -1 to keep to MaxFramesPerSecond, 0 to draw nothing, or a frame rate.", error.Message);
        Assert.Equal(-1, screen.UnfocusedMaxFramesPerSecond);
    }
}
