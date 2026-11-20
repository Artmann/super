using System.Numerics;
using Super.Testing;

namespace Super.Tests;

public class ScriptedInputTests
{
    public enum Control { Jump, Move }

    private class Listener(Input input, Actions<Control> actions) : IGame
    {
        public List<string> Seen { get; } = [];

        public void Load()
        {
            actions.Bind(Control.Jump, Key.Space);
            actions.Bind(Control.Move, Keys.WASD);
        }

        public void Update(GameTime time)
        {
            if (input.Pressed(Key.Space))
            {
                Seen.Add("pressed");
            }

            if (actions.Pressed(Control.Jump))
            {
                Seen.Add("jump");
            }

            if (input.Released(Key.Space))
            {
                Seen.Add("released");
            }

            if (actions.Vector(Control.Move) != Vector2.Zero)
            {
                Seen.Add("move");
            }
        }

        public void Draw(View view)
        {
        }
    }

    private class MouseWatcher(Input input) : IGame
    {
        public Vector2 Position { get; private set; }

        public bool Clicked { get; private set; }

        public void Load()
        {
        }

        public void Update(GameTime time)
        {
            Position = input.Mouse.Position;
            Clicked = input.Pressed(MouseButton.Left);
        }

        public void Draw(View view)
        {
        }
    }

    private class ReadsInDraw(Input input) : IGame
    {
        public void Load()
        {
        }

        public void Update(GameTime time)
        {
        }

        public void Draw(View view) => input.Down(Key.W);
    }

    private class ReadsInLoad(Input input) : IGame
    {
        public void Load() => input.Down(Key.W);

        public void Update(GameTime time)
        {
        }

        public void Draw(View view)
        {
        }
    }

    private class Keyless(Input input) : IGame
    {
        public void Update(GameTime time) => input.Down(Key.W);
    }

    private class Holder(Actions<Control> actions) : IGame
    {
        public Actions<Control> Actions => actions;

        public int TicksHeld { get; private set; }

        public void Update(GameTime time)
        {
            if (actions.Down(Control.Jump))
            {
                TicksHeld++;
            }
        }
    }

    [Fact]
    public void A_script_presses_an_action_for_one_tick_whatever_it_is_bound_to()
    {
        using var run = HeadlessGame.Create<Listener>(1);

        run.Play(new InputScript().Wait(1).Press(Control.Jump));
        run.Ticks(4);

        // No key went down, so only the action shows, and only once.
        Assert.Equal(["jump"], run.Game.Seen);
    }

    [Fact]
    public void A_script_press_lets_go_of_the_action_after_one_tick()
    {
        using var run = HeadlessGame.Create<Holder>(1);

        run.Play(new InputScript().Press(Control.Jump));
        run.Ticks(4);

        Assert.Equal(1, run.Game.TicksHeld);
    }

    [Fact]
    public void A_script_holds_an_action_at_a_vector_until_it_releases_it()
    {
        using var run = HeadlessGame.Create<Listener>(1);

        run.Play(new InputScript().Hold(Control.Move, new Vector2(0f, 1f)).Wait(3).Release(Control.Move));
        run.Ticks(6);

        Assert.Equal(["move", "move", "move"], run.Game.Seen);
    }

    [Fact]
    public void A_script_holds_an_action_as_a_button_until_it_releases_it()
    {
        using var run = HeadlessGame.Create<Holder>(1);

        run.Play(new InputScript().Hold(Control.Jump).Wait(2).Release(Control.Jump));
        run.Ticks(6);

        Assert.Equal(2, run.Game.TicksHeld);
    }

    [Fact]
    public void A_script_action_in_a_game_without_those_actions_says_what_is_missing()
    {
        using var run = HeadlessGame.Create<Keyless>(1);
        run.Play(new InputScript().Press(Control.Jump));

        var error = Assert.Throws<InvalidOperationException>(() => run.Ticks(1));

        Assert.Equal(
            "A script played a Control action, but the game has no Actions<Control>. Inject one and bind it in Load.",
            error.Message);
    }

    [Fact]
    public void Service_hands_out_the_same_service_the_game_was_given()
    {
        using var run = HeadlessGame.Create<Holder>(1);

        Assert.Same(run.Game.Actions, run.Service<Actions<Control>>());
    }

    [Fact]
    public void A_scripted_tap_is_pressed_then_released()
    {
        using var run = HeadlessGame.Create<Listener>(1);
        run.Input.Press(Key.Space);
        run.Ticks(2);
        Assert.Equal(["pressed", "jump", "released"], run.Game.Seen);
    }

    [Fact]
    public void Holding_a_key_lasts_until_released()
    {
        using var run = HeadlessGame.Create<Listener>(1);
        run.Input.Hold(Key.W);
        run.Ticks(3);
        run.Input.Release(Key.W);
        run.Ticks(2);
        Assert.Equal(3, run.Game.Seen.Count(seen => seen == "move"));
    }

    [Fact]
    public void A_scripted_action_press_bypasses_bindings()
    {
        using var run = HeadlessGame.Create<Listener>(1);
        run.Input.Press(Control.Jump);
        run.Ticks(2);
        Assert.Equal(["jump"], run.Game.Seen);
    }

    [Fact]
    public void A_held_action_vector_lasts_until_released()
    {
        using var run = HeadlessGame.Create<Listener>(1);
        run.Input.Hold(Control.Move, new Vector2(1f, 0f));
        run.Ticks(2);
        run.Input.Release(Control.Move);
        run.Ticks(1);
        Assert.Equal(["move", "move"], run.Game.Seen);
    }

    [Fact]
    public void Input_lands_on_the_next_tick_not_a_past_one()
    {
        using var run = HeadlessGame.Create<Listener>(1);
        run.Ticks(5);
        run.Input.Press(Key.Space);
        run.Ticks(1);
        Assert.Equal(["pressed", "jump"], run.Game.Seen);
    }

    [Fact]
    public void Reading_input_in_draw_throws()
    {
        var error = Assert.Throws<InvalidOperationException>(() => HeadlessGame.Create<ReadsInDraw>(1));
        Assert.Contains("only be read in Update", error.Message);
    }

    [Fact]
    public void Reading_input_in_load_throws()
    {
        var error = Assert.Throws<InvalidOperationException>(() => HeadlessGame.Create<ReadsInLoad>(1));
        Assert.Contains("only be read in Update", error.Message);
    }

    [Fact]
    public void Click_moves_the_mouse_then_presses()
    {
        using var run = HeadlessGame.Create<MouseWatcher>(1);
        run.Input.Click(MouseButton.Left, at: new Vector2(100f, 50f));
        run.Ticks(1);
        Assert.Equal(new Vector2(100f, 50f), run.Game.Position);
        Assert.True(run.Game.Clicked);
    }
}
