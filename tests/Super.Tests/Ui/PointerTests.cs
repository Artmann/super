using System.Numerics;
using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

public sealed class PointerGame : IGame
{
    public Func<PointerGame, UiView?> Screen { get; set; } = _ => null;

    public List<string> Log { get; } = [];

    public int Reads { get; set; }

    public Func<Input, bool>? Watch { get; set; }

    public Input Input { get; }

    public PointerGame(Input input) => Input = input;

    public void Update(GameTime time)
    {
        if (Watch?.Invoke(Input) == true)
        {
            Reads++;
        }
    }

    public void Draw(View view)
    {
    }

    public UiView? Ui => Screen(this);
}

public sealed class PointerTests
{
    private static readonly Color Solid = new(0.2f, 0.2f, 0.2f);
    private static readonly Vector2 OnBox = new(40f, 40f);
    private static readonly Vector2 Away = new(600f, 600f);

    private static HeadlessRun<PointerGame> Run(Func<PointerGame, UiView> screen)
    {
        var run = HeadlessGame.Create<PointerGame>(1);
        run.Game.Screen = screen;
        run.Ticks(1);
        return run;
    }

    /// <summary>Puts a view in the window's top left corner, where a test can aim at it.</summary>
    private static UiView Corner(UiView view) =>
        view.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    private static UiView Target(PointerGame game, string name = "click") =>
        Corner(new Text(name).Frame(100f, 100f).Background(Solid).OnClick(() => game.Log.Add(name)));

    private static void MoveTo(HeadlessRun<PointerGame> run, Vector2 at)
    {
        run.Input.MoveMouse(at);
        run.Ticks(1);
    }

    private static void Press(HeadlessRun<PointerGame> run)
    {
        run.Input.Hold(MouseButton.Left);
        run.Ticks(1);
    }

    private static void Release(HeadlessRun<PointerGame> run)
    {
        run.Input.Release(MouseButton.Left);
        run.Ticks(1);
    }

    private static void Click(HeadlessRun<PointerGame> run, Vector2 at)
    {
        MoveTo(run, at);
        Press(run);
        Release(run);
    }

    [Fact]
    public void A_press_and_release_on_a_view_clicks_it()
    {
        using var run = Run(game => Target(game));

        Click(run, OnBox);
        run.Ticks(1);

        Assert.Equal(["click"], run.Game.Log);
    }

    [Fact]
    public void A_release_off_the_view_is_no_click()
    {
        using var run = Run(game => Target(game));

        MoveTo(run, OnBox);
        Press(run);
        MoveTo(run, Away);
        Release(run);
        run.Ticks(1);

        Assert.Empty(run.Game.Log);
    }

    [Fact]
    public void Moving_off_and_back_while_held_still_clicks()
    {
        using var run = Run(game => Target(game));

        MoveTo(run, OnBox);
        Press(run);
        MoveTo(run, Away);
        MoveTo(run, OnBox);
        Release(run);
        run.Ticks(1);

        Assert.Equal(["click"], run.Game.Log);
    }

    [Fact]
    public void A_press_that_began_elsewhere_is_no_click()
    {
        using var run = Run(game => Target(game));

        MoveTo(run, Away);
        Press(run);
        MoveTo(run, OnBox);
        Release(run);
        run.Ticks(1);

        Assert.Empty(run.Game.Log);
    }

    [Fact]
    public void Clicks_within_one_tick_arrive_in_order_and_none_are_lost()
    {
        using var run = Run(game => Corner(new HStack(Spacing: 0f)
        {
            new Text("a").Frame(100f, 100f).Background(Solid).OnClick(() => game.Log.Add("a")),
            new Text("b").Frame(100f, 100f).Background(Solid).OnClick(() => game.Log.Add("b")),
        }));

        run.Input.Click(MouseButton.Left, new Vector2(50f, 50f));
        run.Input.Click(MouseButton.Left, new Vector2(150f, 50f));
        run.Ticks(2);

        Assert.Equal(["a", "b"], run.Game.Log);
    }

    [Fact]
    public void A_click_may_change_game_state_and_read_input()
    {
        bool down = false;
        using var run = Run(game => Corner(new Text("x").Frame(100f, 100f).Background(Solid).OnClick(() => down = game.Input.Down(Key.Space) == false)));

        Click(run, OnBox);
        run.Ticks(1);

        Assert.True(down);
    }

    [Fact]
    public void Hover_and_press_show_in_the_frame_they_happen()
    {
        using var run = Run(_ => Corner(new Pressable(look => new Text(look.Pressed ? "held" : look.Hovered ? "over" : "idle").Frame(100f, 100f).Background(Solid))));

        Assert.Equal("idle", run.ShownText.Single().Text);
        MoveTo(run, OnBox);
        Assert.Equal("over", run.ShownText.Single().Text);
        Press(run);
        Assert.Equal("held", run.ShownText.Single().Text);
        MoveTo(run, Away);
        Assert.Equal("idle", run.ShownText.Single().Text);
        MoveTo(run, OnBox);
        Assert.Equal("held", run.ShownText.Single().Text);
        Release(run);
        Assert.Equal("over", run.ShownText.Single().Text);
    }

    [Fact]
    public void A_disabled_button_stays_quiet_but_blocks()
    {
        using var run = Run(game => Corner(new Button("no", () => game.Log.Add("no"), Disabled: true)));
        run.Game.Watch = input => input.Pressed(MouseButton.Left);

        Click(run, new Vector2(10f, 10f));
        run.Ticks(1);

        Assert.Empty(run.Game.Log);
        Assert.Equal(0, run.Game.Reads);
    }

    [Fact]
    public void The_view_in_front_wins()
    {
        using var run = Run(game => Corner(new ZStack
        {
            new Text("back").Frame(100f, 100f).Background(Solid).OnClick(() => game.Log.Add("back")),
            new Text("front").Frame(100f, 100f).Background(Solid).OnClick(() => game.Log.Add("front")),
        }));

        Click(run, OnBox);
        run.Ticks(1);

        Assert.Equal(["front"], run.Game.Log);
    }

    [Fact]
    public void What_a_scroll_area_clips_cannot_be_clicked()
    {
        using var run = Run(game => Corner(new VStack(Spacing: 0f)
        {
            new ScrollView
            {
                new Text("top").Frame(100f, 100f).Background(Solid),
                new Text("hidden").Frame(100f, 100f).Background(Solid).OnClick(() => game.Log.Add("hidden")),
            }.Frame(100f, 100f),
            new Spacer().Frame(100f, 100f),
        }));

        Click(run, new Vector2(40f, 150f));
        run.Ticks(1);

        Assert.Empty(run.Game.Log);
    }

    [Fact]
    public void Text_alone_lets_the_mouse_through()
    {
        using var run = Run(_ => Corner(new Text("words").Frame(100f, 100f)));
        run.Game.Watch = input => input.Pressed(MouseButton.Left);

        Click(run, OnBox);

        Assert.Equal(1, run.Game.Reads);
    }

    [Fact]
    public void A_solid_box_keeps_the_mouse_from_the_game()
    {
        using var run = Run(_ => Corner(new Text("panel").Frame(100f, 100f).Background(Solid)));
        run.Game.Watch = input => input.Pressed(MouseButton.Left) || input.Scroll != 0f;

        MoveTo(run, OnBox);
        Press(run);
        run.Input.Scroll(1f);
        run.Ticks(1);
        Release(run);

        Assert.Equal(0, run.Game.Reads);
    }

    [Fact]
    public void Ignoring_the_pointer_opts_a_box_out()
    {
        using var run = Run(_ => Corner(new Text("panel").Frame(100f, 100f).Background(Solid).IgnoresPointer()));
        run.Game.Watch = input => input.Pressed(MouseButton.Left);

        Click(run, OnBox);

        Assert.Equal(1, run.Game.Reads);
    }

    [Fact]
    public void Mouse_over_ui_says_where_the_mouse_is_and_position_stays_true()
    {
        bool over = false;
        Vector2 position = default;
        using var run = Run(_ => Corner(new Text("panel").Frame(100f, 100f).Background(Solid)));
        run.Game.Watch = input =>
        {
            over = input.MouseOverUi;
            position = input.MousePosition;
            return false;
        };

        MoveTo(run, OnBox);
        run.Ticks(1);
        Assert.True(over);
        Assert.Equal(OnBox, position);

        MoveTo(run, Away);
        run.Ticks(1);
        Assert.False(over);
    }

    [Fact]
    public void A_press_begun_on_the_interface_stays_there_after_leaving_it()
    {
        using var run = Run(_ => Corner(new Text("panel").Frame(100f, 100f).Background(Solid)));
        run.Game.Watch = input => input.Down(MouseButton.Left) || input.Released(MouseButton.Left);

        MoveTo(run, OnBox);
        Press(run);
        MoveTo(run, Away);
        Release(run);

        Assert.Equal(0, run.Game.Reads);
    }

    [Fact]
    public void A_press_begun_in_the_game_stays_the_games_over_the_interface()
    {
        bool hovered = false;
        using var run = Run(_ => Corner(new Pressable(look =>
        {
            hovered |= look.Hovered;
            return new Text("x").Frame(100f, 100f).Background(Solid);
        })));
        run.Game.Watch = input => input.Released(MouseButton.Left);

        MoveTo(run, Away);
        Press(run);
        MoveTo(run, OnBox);
        Assert.False(hovered);

        Release(run);

        Assert.Equal(1, run.Game.Reads);
    }

    [Fact]
    public void A_captured_mouse_never_touches_the_interface()
    {
        using var run = Run(game => Target(game));
        run.Game.Input.CaptureMouse = true;
        run.Game.Watch = input => input.Pressed(MouseButton.Left);

        Click(run, OnBox);
        run.Ticks(1);

        Assert.Empty(run.Game.Log);
        Assert.Equal(1, run.Game.Reads);
    }

    [Fact]
    public void A_press_on_something_that_goes_away_is_dropped()
    {
        using var run = Run(game => game.Reads > 0 ? new Text("gone") : Target(game));

        MoveTo(run, OnBox);
        Press(run);
        run.Game.Reads = 1;
        run.Ticks(1);
        Release(run);
        run.Ticks(1);

        Assert.Empty(run.Game.Log);
    }

    [Fact]
    public void Margins_stay_out_of_reach()
    {
        using var run = Run(game => Corner(new Text("x").Frame(50f, 50f).Background(Solid).Margin(40f).OnClick(() => game.Log.Add("x"))));

        Click(run, new Vector2(10f, 10f));
        run.Ticks(1);
        Assert.Empty(run.Game.Log);

        Click(run, new Vector2(60f, 60f));
        run.Ticks(1);
        Assert.Equal(["x"], run.Game.Log);
    }
}
