using System.Numerics;
using Super.Testing;
using Super.Ui;

namespace Super.Tests.Ui;

/// <summary>Finding a view on screen by its id, so tests and scripts don't redo the layout by hand.</summary>
public sealed class FindByIdTests
{
    private static readonly Color Solid = new(0.2f, 0.2f, 0.2f);

    private static HeadlessRun<PointerGame> Run(Func<PointerGame, UiView> screen)
    {
        var run = HeadlessGame.Create<PointerGame>(1);
        run.Game.Screen = screen;
        run.Ticks(1);
        return run;
    }

    private static UiView Corner(UiView view) =>
        view.Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity, alignment: Alignment.TopLeading);

    private static UiView Button(PointerGame game, string name, float width = 100f) =>
        new Text(name).Frame(width, 40f).Background(Solid).OnClick(() => game.Log.Add(name)).Id(name);

    [Fact]
    public void FrameOf_gives_where_the_view_with_that_id_was_laid_out()
    {
        using var run = Run(game => Corner(new HStack(Spacing: 10f) { Button(game, "a", 70f), Button(game, "b") }));

        Assert.Equal(new UiRect(80f, 0f, 100f, 40f), run.FrameOf("b"));
    }

    [Fact]
    public void FrameOf_leaves_out_the_margin_the_background_does_not_fill()
    {
        using var run = Run(_ => Corner(new Text("x").Frame(100f, 40f).Background(Solid).Margin(12f).Id("card")));

        Assert.Equal(new UiRect(12f, 12f, 100f, 40f), run.FrameOf("card"));
    }

    [Fact]
    public void FrameOf_finds_an_id_set_on_a_component()
    {
        using var run = Run(_ => Corner(new VStack(Spacing: 0f) { new Text("top").Frame(50f, 30f), new Card().Id("card") }));

        Assert.Equal(new UiRect(5f, 35f, 60f, 20f), run.FrameOf("card"));
    }

    [Fact]
    public void FrameOf_an_id_not_on_screen_says_which_ids_are()
    {
        using var run = Run(game => Corner(new HStack { Button(game, "a"), Button(game, "b") }));

        var problem = Assert.Throws<InvalidOperationException>(() => run.FrameOf("c"));

        Assert.Equal("No view on screen has the id 'c'. These ids are: a, b.", problem.Message);
    }

    [Fact]
    public void FrameOf_an_id_on_two_views_is_an_error()
    {
        using var run = Run(game => Corner(new VStack
        {
            new HStack { Button(game, "a") },
            new HStack { Button(game, "a") },
        }));

        var problem = Assert.Throws<InvalidOperationException>(() => run.FrameOf("a"));

        Assert.Equal("2 views on screen have the id 'a', so it doesn't say which one.", problem.Message);
    }

    [Fact]
    public void Clicking_an_id_clicks_that_view()
    {
        using var run = Run(game => Corner(new HStack(Spacing: 10f) { Button(game, "a"), Button(game, "b") }));

        run.Input.Click("b");
        run.Ticks(4);

        Assert.Equal(["b"], run.Game.Log);
    }

    [Fact]
    public void A_script_finds_the_id_when_the_step_runs_not_when_it_was_written()
    {
        // The button only exists from tick 3, and moves at tick 6.
        using var run = Run(game => Corner(new HStack
        {
            new Text("spacer").Frame(game.Ticks >= 6 ? 300f : 10f, 40f),
            game.Ticks >= 3 ? Button(game, "late") : null,
        }));
        run.Play(new InputScript().Wait(8).Click("late"));

        run.Ticks(14);

        Assert.Equal(["late"], run.Game.Log);
    }

    [Fact]
    public void A_script_clicking_an_id_that_is_not_there_says_so()
    {
        using var run = Run(game => Corner(Button(game, "a")));
        run.Play(new InputScript().Click("missing"));

        var problem = Assert.Throws<InvalidOperationException>(() => run.Ticks(3));

        Assert.Equal("No view on screen has the id 'missing'. These ids are: a.", problem.Message);
    }

    [Fact]
    public void A_script_can_press_a_key()
    {
        using var run = Run(_ => new Text("x"));
        run.Game.Watch = input => input.Pressed(Key.Space);
        run.Play(new InputScript().Wait(2).Press(Key.Space));

        run.Ticks(6);

        Assert.Equal(1, run.Game.Reads);
    }

    [Fact]
    public void A_script_can_hold_a_key_until_it_lets_go()
    {
        using var run = Run(_ => new Text("x"));
        run.Game.Watch = input => input.Down(Key.W);
        run.Play(new InputScript().Hold(Key.W).Wait(4).Release(Key.W));

        run.Ticks(10);

        Assert.Equal(4, run.Game.Reads);
    }

    private sealed record Card : UiView
    {
        public override UiView Body => new Text("card").Frame(60f, 20f).Background(Solid).Margin(5f);
    }
}
