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
            Position = input.MousePosition;
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
        using var run = HeadlessGame.Create<ReadsInDraw>(1);
        var error = Assert.Throws<InvalidOperationException>(() => run.Ticks(1));
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
