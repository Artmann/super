using System.Numerics;

namespace Super.Tests;

public class ActionsTests
{
    private enum Control { Jump, Move, Look, Zoom }

    private enum Small : byte { A }

    private static readonly bool[] NoButtons = new bool[15];

    private static (Input Input, Actions<Control> Actions) Create()
    {
        var input = new Input { Readable = true };
        return (input, new Actions<Control>(input));
    }

    private static float[] Axes(float leftX = 0f, float leftY = 0f, float rightX = 0f, float rightY = 0f, float leftTrigger = -1f, float rightTrigger = -1f) =>
        [leftX, leftY, rightX, rightY, leftTrigger, rightTrigger];

    [Fact]
    public void Two_taps_in_back_to_back_ticks_are_two_action_presses()
    {
        var (input, actions) = Create();
        actions.Bind(Control.Jump, Key.Space);

        input.KeyDown(Key.Space);
        input.KeyUp(Key.Space);
        input.Roll();
        Assert.True(actions.Pressed(Control.Jump));

        input.KeyDown(Key.Space);
        input.KeyUp(Key.Space);
        input.Roll();
        Assert.True(actions.Pressed(Control.Jump));
        Assert.True(actions.Down(Control.Jump));

        input.Roll();
        Assert.False(actions.Pressed(Control.Jump));
        Assert.True(actions.Released(Control.Jump));
    }

    [Fact]
    public void A_button_binding_presses_and_releases()
    {
        var (input, actions) = Create();
        actions.Bind(Control.Jump, Key.Space, GamepadButton.South);

        input.KeyDown(Key.Space);
        input.Roll();
        Assert.True(actions.Pressed(Control.Jump));
        Assert.True(actions.Down(Control.Jump));
        Assert.Equal(1f, actions.Value(Control.Jump));

        input.KeyUp(Key.Space);
        input.Roll();
        Assert.True(actions.Released(Control.Jump));
        Assert.False(actions.Down(Control.Jump));
    }

    [Fact]
    public void Any_of_several_bindings_fires_the_action()
    {
        var (input, actions) = Create();
        actions.Bind(Control.Jump, Key.Space, GamepadButton.South);

        var buttons = new bool[15];
        buttons[(int)GamepadButton.South] = true;
        input.Gamepad(true, Axes(), buttons);
        input.Roll();
        Assert.True(actions.Pressed(Control.Jump));
    }

    [Fact]
    public void WASD_gives_a_unit_vector_with_forward_positive()
    {
        var (input, actions) = Create();
        actions.Bind(Control.Move, Keys.WASD);

        input.KeyDown(Key.W);
        input.KeyDown(Key.D);
        input.Roll();
        var expected = Vector2.Normalize(new Vector2(1f, 1f));
        Assert.Equal(expected.X, actions.Vector(Control.Move).X, 1e-5f);
        Assert.Equal(expected.Y, actions.Vector(Control.Move).Y, 1e-5f);
        Assert.Equal(1f, actions.Value(Control.Move), 1e-5f);
    }

    [Fact]
    public void Opposite_keys_cancel_to_zero()
    {
        var (input, actions) = Create();
        actions.Bind(Control.Move, Keys.WASD);

        input.KeyDown(Key.W);
        input.KeyDown(Key.S);
        input.Roll();
        Assert.Equal(Vector2.Zero, actions.Vector(Control.Move));
        Assert.False(actions.Down(Control.Move));
        Assert.False(float.IsNaN(actions.Value(Control.Move)));
    }

    [Fact]
    public void Keys_and_a_stick_on_one_action_sum_and_clamp()
    {
        var (input, actions) = Create();
        actions.Bind(Control.Move, Keys.WASD, GamepadStick.Left);

        input.KeyDown(Key.W);
        input.Gamepad(true, Axes(leftY: -1f), NoButtons);
        input.Roll();
        Assert.Equal(new Vector2(0f, 1f), actions.Vector(Control.Move));
    }

    [Fact]
    public void Mouse_delta_is_scaled_and_never_clamped()
    {
        var (input, actions) = Create();
        actions.Bind(Control.Look, Mouse.Delta.Scaled(0.5f), GamepadStick.Right);

        input.MouseMoved(Vector2.Zero);
        input.MouseMoved(new Vector2(40f, 0f));
        input.Roll();
        Assert.Equal(new Vector2(20f, 0f), actions.Vector(Control.Look));
    }

    [Fact]
    public void An_axis_over_triggers_runs_from_minus_one_to_one()
    {
        var (input, actions) = Create();
        actions.Bind(Control.Zoom, Binding.Axis(GamepadTrigger.Left, GamepadTrigger.Right));

        input.Gamepad(true, Axes(rightTrigger: 1f), NoButtons);
        input.Roll();
        Assert.Equal(1f, actions.Vector(Control.Zoom).X);

        input.Gamepad(true, Axes(leftTrigger: 1f), NoButtons);
        input.Roll();
        Assert.Equal(-1f, actions.Vector(Control.Zoom).X);
    }

    [Fact]
    public void Down_on_an_axis_action_means_above_half()
    {
        var (input, actions) = Create();
        actions.Bind(Control.Move, GamepadStick.Left);

        input.Gamepad(true, Axes(leftY: -0.7f), NoButtons);
        input.Roll();
        Assert.True(actions.Down(Control.Move));

        input.Gamepad(true, Axes(leftY: -0.3f), NoButtons);
        input.Roll();
        Assert.False(actions.Down(Control.Move));
    }

    [Fact]
    public void An_override_beats_the_bindings()
    {
        var (input, actions) = Create();
        actions.Bind(Control.Move, Keys.WASD);

        input.KeyDown(Key.W);
        actions.Override(Control.Move, new Vector2(1f, 0f), button: false);
        input.Roll();
        Assert.Equal(new Vector2(1f, 0f), actions.Vector(Control.Move));

        actions.ClearOverride(Control.Move);
        input.Roll();
        Assert.Equal(new Vector2(0f, 1f), actions.Vector(Control.Move));
    }

    [Fact]
    public void Unbind_clears_and_bindings_lists_what_is_bound()
    {
        var (input, actions) = Create();
        actions.Bind(Control.Jump, Key.Space);
        Assert.Single(actions.Bindings(Control.Jump));

        actions.Unbind(Control.Jump);
        Assert.Empty(actions.Bindings(Control.Jump));

        input.KeyDown(Key.Space);
        input.Roll();
        Assert.False(actions.Down(Control.Jump));
    }

    [Fact]
    public void A_control_enum_that_is_not_int_sized_is_rejected()
    {
        var input = new Input { Readable = true };
        Assert.Throws<ArgumentException>(() => new Actions<Small>(input));
    }

    [Fact]
    public void Axis_rejects_a_stick_operand()
    {
        Assert.Throws<ArgumentException>(() => Binding.Axis(GamepadStick.Left, Key.A));
    }
}
