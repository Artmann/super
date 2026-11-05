using System.Globalization;
using System.Numerics;
using Rendition;
using Rendition.Passes;
using Super.Testing;

namespace Super.Tests;

public class TransitionTests
{
    private const float Tolerance = 0.0001f;

    private static Camera At(float x) => new(new Vector3(x, 0f, 0f), new Vector3(x, 0f, -1f));

    /// <summary>
    /// Plays a transition on tick 10, at 30 ticks a second. Its camera stands at x 0 and
    /// moves to x 30 in the change the transition covers.
    /// </summary>
    private sealed class Door(Transitions transitions, GameTime time) : IGame
    {
        private float _x;

        public static Transition Next { get; set; } = Transition.Cut;

        public long? CutTick { get; private set; }

        public Transitions Transitions => transitions;

        public void Update(GameTime gameTime)
        {
            if (gameTime.Tick == 10)
            {
                transitions.Play(Next, () =>
                {
                    CutTick = time.Tick;
                    _x = 30f;
                });
            }
        }

        public void Draw(View view) => view.Camera(At(_x));
    }

    private static HeadlessRun<Door> Start(Transition transition)
    {
        Door.Next = transition;
        return HeadlessGame.Create<Door>(1);
    }

    [Fact]
    public void A_fade_makes_its_change_on_the_tick_it_reaches_halfway()
    {
        using var run = Start(Transition.Fade(1f));

        run.Ticks(30);

        // Ticks run 1/30 s apart, so half a second after tick 10 is tick 25.
        Assert.Equal(25L, run.Game.CutTick);
    }

    [Fact]
    public void A_fade_goes_out_to_its_colour_and_back_in()
    {
        using var run = Start(Transition.Fade(1f, new Color(0.5f, 0.5f, 0.5f)));

        run.Ticks(11);
        run.Frame(0.5f);
        Assert.Equal(TransitionStyle.Fade, run.Transition.Style);
        Assert.Equal(0f, run.Transition.Amount, Tolerance);

        // Middle grey as written is ((0.5 + 0.055) / 1.055)^2.4 = 0.214 as light.
        Assert.Equal(0.21404f, run.Transition.Color.X, Tolerance);

        // Two and a half ticks in is 1/12 s, a sixth of the half second going out.
        run.Ticks(3);
        run.Frame(0.5f);
        Assert.Equal(1f / 6f, run.Transition.Amount, Tolerance);

        // Half a second in, all colour.
        run.Ticks(13);
        run.Frame(0f);
        Assert.Equal(1f, run.Transition.Amount, Tolerance);
    }

    [Fact]
    public void A_fade_ends_once_its_frames_have_come_all_the_way_back()
    {
        using var run = Start(Transition.Fade(1f));

        // 29.5 ticks in, 1/30 s is left of the half second coming in.
        run.Ticks(41);
        run.Frame(0.5f);
        Assert.True(run.Game.Transitions.IsPlaying);
        Assert.Equal(1f / 30f, run.Transition.Amount, Tolerance);

        run.Ticks(1);
        run.Frame(0.5f);
        Assert.False(run.Game.Transitions.IsPlaying);
        Assert.Equal(TransitionStyle.None, run.Transition.Style);
    }

    [Fact]
    public void A_dissolve_makes_its_change_at_once_and_holds_one_picture()
    {
        using var run = Start(Transition.Dissolve(1f));
        run.Ticks(10);
        run.Frame(0f);
        int before = run.Transition.Hold;

        run.Ticks(1);
        Assert.Equal(10L, run.Game.CutTick);
        run.Frame(0f);
        int during = run.Transition.Hold;
        run.Ticks(5);
        run.Frame(0.5f);

        Assert.Equal(before + 1, during);
        Assert.Equal(during, run.Transition.Hold);
        Assert.Equal(TransitionStyle.Dissolve, run.Transition.Style);

        // 4.5 ticks of 30 is 0.15 s of the second.
        Assert.Equal(0.15f, run.Transition.Amount, Tolerance);
    }

    [Fact]
    public void A_wipe_tells_the_screen_its_direction_and_edge()
    {
        using var run = Start(Transition.Wipe(1f, WipeDirection.Up, softness: 0.2f));

        run.Ticks(12);
        run.Frame(0f);

        Assert.Equal(TransitionStyle.Wipe, run.Transition.Style);
        Assert.Equal(new Vector2(0f, -1f), run.Transition.Direction);
        Assert.Equal(0.2f, run.Transition.Softness);
    }

    [Fact]
    public void An_iris_tells_the_screen_where_it_opens()
    {
        using var run = Start(Transition.Iris(1f, new Vector2(0.25f, 0.75f)));

        run.Ticks(12);
        run.Frame(0f);

        Assert.Equal(TransitionStyle.Iris, run.Transition.Style);
        Assert.Equal(new Vector2(0.25f, 0.75f), run.Transition.Center);
    }

    [Fact]
    public void A_blend_moves_the_camera_from_the_last_one_shown()
    {
        using var run = Start(Transition.Blend(1f, Easing.Linear));
        run.Ticks(10);
        run.Frame(0f);

        run.Ticks(4);
        run.Frame(0.5f);

        // 2.5 ticks is 1/12 of the second, so 1/12 of the way from x 0 to x 30.
        Assert.Equal(2.5f, run.Camera.Position.X, Tolerance);
        Assert.Equal(TransitionStyle.None, run.Transition.Style);
    }

    [Fact]
    public void Playing_another_makes_the_waiting_fade_change_first()
    {
        using var run = Start(Transition.Fade(1f));
        run.Ticks(12);
        Assert.Null(run.Game.CutTick);

        run.Game.Transitions.Play(Transition.Cut);

        Assert.NotNull(run.Game.CutTick);
        Assert.False(run.Game.Transitions.IsPlaying);
    }

    /// <summary>Two one-second shots, the second at x 100, with a transition between them.</summary>
    private sealed class Film(Director director, Transitions transitions) : IGame
    {
        public static Func<Cutscene> Make { get; set; } = () => new Cutscene();

        public Transitions Transitions => transitions;

        public Director Director => director;

        public void Load() => director.Play(Make());

        public void Update(GameTime time)
        {
        }

        public void Draw(View view) => view.Camera(At(-50f));
    }

    private static HeadlessRun<Film> Film_(Func<Cutscene> make)
    {
        Film.Make = make;
        return HeadlessGame.Create<Film>(1);
    }

    [Fact]
    public void A_fade_between_shots_starts_half_its_length_before_the_cut()
    {
        using var run = Film_(() => new Cutscene()
            .Then(Shot.Hold(1f, At(0f)))
            .Then(Shot.Hold(1f, At(100f)), Transition.Fade(0.6f)));

        // Tick 20 is at 0.667 s and tick 21 at 0.7 s, where the fade starts.
        run.Ticks(21);
        Assert.False(run.Game.Transitions.IsPlaying);
        run.Ticks(1);
        Assert.True(run.Game.Transitions.IsPlaying);

        // Tick 31 at 1.033 s is in the second shot; its frame shows 0.3 s in, all black.
        run.Ticks(10);
        run.Frame(0f);
        Assert.Equal(100f, run.Camera.Position.X, Tolerance);
        Assert.Equal(1f, run.Transition.Amount, Tolerance);
    }

    [Fact]
    public void A_fade_into_the_first_shot_comes_in_from_its_colour()
    {
        using var run = Film_(() => new Cutscene()
            .Then(Shot.Hold(1f, At(0f)), Transition.Fade(0.6f)));

        run.Ticks(1);
        run.Frame(0f);
        Assert.Equal(1f, run.Transition.Amount, Tolerance);

        // 2.5 ticks is 1/12 s of the 0.3 s coming in.
        run.Ticks(3);
        run.Frame(0.5f);
        Assert.Equal(1f - (1f / 12f) / 0.3f, run.Transition.Amount, Tolerance);
    }

    [Fact]
    public void A_blend_into_the_first_shot_starts_from_the_games_camera()
    {
        using var run = Film_(() => new Cutscene()
            .Then(Shot.Hold(1f, At(100f)), Transition.Blend(0.5f, Easing.Linear)));
        run.Frame(0f);
        Assert.Equal(-50f, run.Camera.Position.X, Tolerance);

        // It starts on tick 0, so after tick 3 a frame halfway shows 2.5 ticks, 1/12 s: a sixth
        // of the half second, so a sixth of the way from -50 to 100.
        run.Ticks(4);
        run.Frame(0.5f);

        Assert.Equal(-25f, run.Camera.Position.X, Tolerance);
    }

    [Fact]
    public void A_dissolve_between_shots_starts_on_the_cut()
    {
        using var run = Film_(() => new Cutscene()
            .Then(Shot.Hold(1f, At(0f)))
            .Then(Shot.Hold(1f, At(100f)), Transition.Dissolve(0.5f)));

        // Tick 30 is the first at 1 s.
        run.Ticks(30);
        Assert.False(run.Game.Transitions.IsPlaying);
        run.Ticks(1);
        Assert.True(run.Game.Transitions.IsPlaying);
        Assert.Equal(TransitionKind.Dissolve, run.Game.Transitions.Playing!.Kind);
    }

    [Fact]
    public void Ending_on_its_own_plays_the_end_transition_back_to_the_game()
    {
        using var run = Film_(() => new Cutscene()
            .Then(Shot.Hold(0.51f, At(0f)))
            .End(Transition.Blend(1f)));

        // Tick 16 at 0.533 s is the first past the end. A blend needs a camera shown to start from.
        run.Ticks(16);
        run.Frame(0f);
        Assert.True(run.Game.Director.IsPlaying);
        Assert.False(run.Game.Transitions.IsPlaying);
        run.Ticks(1);

        Assert.False(run.Game.Director.IsPlaying);
        Assert.Equal(TransitionKind.Blend, run.Game.Transitions.Playing!.Kind);
    }

    [Fact]
    public void Stopping_a_cutscene_skips_its_end_transition()
    {
        using var run = Film_(() => new Cutscene()
            .Then(Shot.Hold(0.5f, At(0f)))
            .End(Transition.Dissolve(1f)));
        run.Ticks(3);

        run.Game.Director.Stop();
        run.Ticks(20);

        Assert.False(run.Game.Transitions.IsPlaying);
    }

    [Fact]
    public void A_transition_must_take_time()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Transition.Dissolve(0f));

        Assert.StartsWith("A transition must last more than zero seconds.", error.Message);
    }

    [Fact]
    public void An_iris_must_open_on_the_screen()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Transition.Iris(1f, new Vector2(1.5f, 0.5f)));

        Assert.StartsWith("An iris opens from a point on the screen, with x and y from 0 to 1.", error.Message);
    }

    [Fact]
    public void An_edge_cannot_be_less_than_sharp()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => Transition.Wipe(1f, softness: -0.1f));

        Assert.StartsWith("An edge's softness must be a number of at least zero.", error.Message);
    }

    [Fact]
    public void A_fade_must_fit_in_the_shot_before_it_whatever_the_language()
    {
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var cutscene = new Cutscene().Then(Shot.Hold(0.25f, At(0f)));

            var error = Assert.Throws<ArgumentException>(() => cutscene.Then(Shot.Hold(1f, At(1f)), Transition.Fade(1f)));

            Assert.StartsWith("The fade's first half, 0.5 seconds, must fit in the shot before it, which lasts 0.25.", error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void A_transition_must_fit_in_the_shot_it_leads_into()
    {
        var cutscene = new Cutscene().Then(Shot.Hold(1f, At(0f)));

        var error = Assert.Throws<ArgumentException>(() => cutscene.Then(Shot.Hold(0.5f, At(1f)), Transition.Wipe(0.75f)));

        Assert.StartsWith("The transition's 0.75 seconds into the shot must fit in it, and it lasts 0.5.", error.Message);
    }

    [Fact]
    public void Shots_cannot_follow_the_end()
    {
        var cutscene = new Cutscene().Then(Shot.Hold(1f, At(0f))).End(Transition.Dissolve(0.5f));

        var error = Assert.Throws<InvalidOperationException>(() => cutscene.Then(Shot.Hold(1f, At(1f))));

        Assert.Equal("The cutscene's end is already set. Add every shot before End.", error.Message);
    }

    [Fact]
    public void The_screen_gets_a_direction_one_long_and_an_amount_from_0_to_1()
    {
        var constants = TransitionConstants.From(new ScreenTransition
        {
            Style = TransitionStyle.Wipe,
            Amount = 1.5f,
            Direction = new Vector2(3f, 4f),
            Center = new Vector2(0.25f, 0.75f),
            Softness = -1f,
            Color = new Vector3(-1f, 0.5f, 2f),
        });

        Assert.Equal(new Vector4(0f, 0.5f, 2f, 1f), constants.ColorAmount);
        Assert.Equal(new Vector4(0.6f, 0.8f, 0.25f, 0.75f), constants.Shape);
        Assert.Equal(new Vector4(3f, 0f, 0f, 0f), constants.Style);
    }

    [Fact]
    public void A_wipe_without_a_direction_goes_right_and_a_broken_amount_shows_nothing()
    {
        var constants = TransitionConstants.From(new ScreenTransition { Amount = float.NaN });

        Assert.Equal(new Vector2(1f, 0f), new Vector2(constants.Shape.X, constants.Shape.Y));
        Assert.Equal(0f, constants.ColorAmount.W);
    }

    [Fact]
    public void The_screen_settings_fill_three_rows_of_four_floats()
    {
        // finish.frag's push constant block is three vec4s.
        Assert.Equal(48u, TransitionConstants.Size);
    }
}
