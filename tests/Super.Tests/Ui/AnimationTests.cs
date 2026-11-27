using System.Globalization;
using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>UseAnimation, UseTime, transitions and the controls that move, on the headless clock of 30 ticks a second.</summary>
public sealed class AnimationTests
{
    /// <summary>How many times a component's Body ran.</summary>
    private sealed class Runs
    {
        public int Count;
    }

    /// <summary>Shows where an animation heading for the target is, to three places.</summary>
    private sealed record Mover(float Target, float Seconds = 0.3f, float? From = null, Runs? Counted = null) : UiView
    {
        public override UiView Body
        {
            get
            {
                if (Counted is not null)
                {
                    Counted.Count++;
                }

                float at = UseAnimation(Target, Seconds, Easing.Linear, From);
                return new Text(at.ToString("0.000", CultureInfo.InvariantCulture)).Id("at");
            }
        }
    }

    private sealed class Screen
    {
        public UiView View { get; set; } = new Mover(0f);
    }

    private static HeadlessRun<PointerGame> Run(Screen screen) =>
        PointerGame.Show(_ => screen.View.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading));

    private static float At(HeadlessRun<PointerGame> run) =>
        float.Parse(run.ShownText.Single(shown => shown.Text.Contains('.')).Text, CultureInfo.InvariantCulture);

    [Fact]
    public void An_animation_starts_at_its_target()
    {
        using var run = Run(new Screen { View = new Mover(5f) });

        Assert.Equal(5f, At(run));
    }

    [Fact]
    public void A_new_target_is_reached_by_the_seconds_given_moving_evenly_on_the_way()
    {
        var screen = new Screen();
        using var run = Run(screen);

        // 0.3 seconds is 9 ticks. A linear ease has gone a third of the way after 3 of them.
        screen.View = new Mover(9f);
        run.Ticks(1);
        float first = At(run);
        run.Ticks(3);
        float later = At(run);
        run.Ticks(6);

        Assert.Equal(0f, first);
        Assert.Equal(3f, later, 0.01f);
        Assert.Equal(9f, At(run));
    }

    [Fact]
    public void A_target_changed_halfway_turns_round_from_where_it_is()
    {
        var screen = new Screen();
        using var run = Run(screen);
        screen.View = new Mover(9f);
        run.Ticks(5);
        float halfway = At(run);

        screen.View = new Mover(0f);
        run.Ticks(1);

        // On the frame it turns, it's where it had got to by then, a tick's move further on, so nothing jumps.
        Assert.InRange(halfway, 3f, 6f);
        Assert.Equal(halfway + 1f, At(run), 0.01f);
        run.Ticks(3);
        Assert.True(At(run) < halfway);
    }

    [Fact]
    public void An_animation_from_a_value_moves_in_when_the_view_appears()
    {
        using var run = Run(new Screen { View = new Mover(1f, From: 0f) });

        float first = At(run);
        run.Ticks(10);

        Assert.True(first < 0.5f, $"It showed {first} on its first frame.");
        Assert.Equal(1f, At(run));
    }

    [Fact]
    public void Inside_animations_off_a_new_target_shows_at_once()
    {
        var screen = new Screen { View = new Mover(0f).Animations(false) };
        using var run = Run(screen);

        screen.View = new Mover(9f).Animations(false);
        run.Ticks(1);

        Assert.Equal(9f, At(run));
    }

    [Fact]
    public void Body_stops_running_once_the_animation_arrives()
    {
        var runs = new Runs();
        var screen = new Screen { View = new Mover(0f, Counted: runs) };
        using var run = Run(screen);
        screen.View = new Mover(1f, Counted: runs);
        run.Ticks(12);
        int arrived = runs.Count;

        run.Ticks(10);

        Assert.True(arrived > 5, $"Body ran {arrived} times on the way.");
        Assert.Equal(arrived, runs.Count);
    }

    [Fact]
    public void A_rising_view_starts_low_and_faded_and_settles_in_its_place()
    {
        var screen = new Screen { View = new Text("Anchor") };
        using var run = Run(screen);
        screen.View = new VStack(HorizontalAlignment.Leading, 0f) { new Text("Anchor"), new Text("Saved").Transition(Entrance.Rise) };
        run.Ticks(1);
        var first = run.ShownText.Single(shown => shown.Text == "Saved");

        run.Ticks(10);
        var settled = run.ShownText.Single(shown => shown.Text == "Saved");

        Assert.True(first.Frame.Y > settled.Frame.Y, $"It started at {first.Frame.Y} and settled at {settled.Frame.Y}.");
        Assert.True(first.Color.A < 0.5f, $"It started at an alpha of {first.Color.A}.");
        Assert.Equal(1f, settled.Color.A);

        // Only where it's drawn moves: it took its full room from the first frame.
        Assert.Equal(run.ShownText.Single(shown => shown.Text == "Anchor").Frame.Bottom, settled.Frame.Y, 0.01f);
    }

    [Fact]
    public void A_transition_keeps_the_state_of_what_is_inside_once_it_arrives()
    {
        var screen = new Screen { View = new Text("Anchor") };
        using var run = Run(screen);
        screen.View = new DisclosureGroup("More") { new Text("Inside") }.Transition(Entrance.Fade, 0.5f);
        run.Ticks(1);

        // Opened on the way in, it keeps itself open: nothing underneath is remade when the fade arrives.
        var header = run.Elements.Single(element => element.Label == "More").Frame;
        PointerGame.Click(run, new System.Numerics.Vector2(header.X + 10f, header.Y + header.Height / 2f));
        Assert.True(run.Animating);
        run.Ticks(20);

        Assert.False(run.Animating);
        Assert.Contains(run.ShownText, shown => shown.Text == "Inside");
    }

    [Fact]
    public void A_spinning_progress_view_keeps_the_interface_moving()
    {
        var runs = new Runs();
        var screen = new Screen { View = new VStack { new ProgressView("Loading"), new Mover(0f, Counted: runs) } };
        using var run = Run(screen);

        run.Ticks(3);

        var spinner = run.Elements.Single(element => element.Role == UiRole.ProgressBar);
        Assert.Equal("Loading", spinner.Label);
        Assert.True(run.Animating);

        // Only the spinner runs again, not what's beside it.
        Assert.Equal(1, runs.Count);
    }

    [Fact]
    public void Inside_animations_off_a_spinning_progress_view_stands_still()
    {
        var screen = new Screen { View = new ProgressView("Loading").Animations(false) };
        using var run = Run(screen);

        run.Ticks(3);

        Assert.Contains(run.Elements, element => element.Role == UiRole.ProgressBar);
        Assert.False(run.Animating);
    }

    [Fact]
    public void A_switch_says_it_is_on_at_once_while_its_knob_is_still_on_the_way()
    {
        var screen = new Screen { View = new Toggle("Music", false) };
        using var run = Run(screen);

        screen.View = new Toggle("Music", true);
        run.Ticks(1);

        Assert.True(run.Elements.Single(element => element.Label == "Music").Checked);
        Assert.True(run.Animating);
        run.Ticks(10);
        Assert.False(run.Animating);
    }
}
