using System.Numerics;

namespace Super.Tests;

public class InputTests
{
    private static Input Create() => new() { Readable = true };

    private static readonly bool[] NoButtons = new bool[15];

    private static float[] Axes(float leftX = 0f, float leftY = 0f, float rightX = 0f, float rightY = 0f, float leftTrigger = -1f, float rightTrigger = -1f) =>
        [leftX, leftY, rightX, rightY, leftTrigger, rightTrigger];

    [Fact]
    public void A_held_key_is_pressed_once_then_down()
    {
        var input = Create();
        input.KeyDown(Key.W);
        input.Roll();
        Assert.True(input.Pressed(Key.W));
        Assert.True(input.Down(Key.W));

        input.Roll();
        Assert.False(input.Pressed(Key.W));
        Assert.True(input.Down(Key.W));

        input.KeyUp(Key.W);
        input.Roll();
        Assert.True(input.Released(Key.W));
        Assert.False(input.Down(Key.W));
    }

    [Fact]
    public void A_tap_shorter_than_a_tick_is_pressed_then_released_on_the_next_tick()
    {
        var input = Create();
        input.KeyDown(Key.Space);
        input.KeyUp(Key.Space);

        input.Roll();
        Assert.True(input.Pressed(Key.Space));
        Assert.True(input.Down(Key.Space));
        Assert.False(input.Released(Key.Space));

        input.Roll();
        Assert.True(input.Released(Key.Space));
        Assert.False(input.Down(Key.Space));
        Assert.False(input.Pressed(Key.Space));

        input.Roll();
        Assert.False(input.Released(Key.Space));
        Assert.False(input.Down(Key.Space));
        Assert.False(input.Pressed(Key.Space));
    }

    [Fact]
    public void A_repeat_while_held_is_not_a_new_press()
    {
        var input = Create();
        input.KeyDown(Key.W);
        input.Roll();

        input.KeyDown(Key.W);
        input.Roll();
        Assert.False(input.Pressed(Key.W));
        Assert.True(input.Down(Key.W));
    }

    [Fact]
    public void Mouse_delta_and_scroll_sum_within_a_tick_and_reset()
    {
        var input = Create();
        input.MouseMoved(new Vector2(10f, 10f));
        input.MouseMoved(new Vector2(15f, 12f));
        input.Scrolled(1f);
        input.Scrolled(1f);

        input.Roll();
        Assert.Equal(new Vector2(5f, 2f), input.MouseDelta);
        Assert.Equal(2f, input.Scroll);
        Assert.Equal(new Vector2(15f, 12f), input.MousePosition);

        input.Roll();
        Assert.Equal(Vector2.Zero, input.MouseDelta);
        Assert.Equal(0f, input.Scroll);
        Assert.Equal(new Vector2(15f, 12f), input.MousePosition);
    }

    [Fact]
    public void Typed_text_is_the_characters_since_the_last_tick()
    {
        var input = Create();
        input.TypedCharacter('h');
        input.TypedCharacter('i');
        input.Roll();
        Assert.Equal("hi", input.Typed);

        input.Roll();
        Assert.Equal("", input.Typed);
    }

    [Fact]
    public void A_small_stick_is_inside_the_dead_zone()
    {
        var input = Create();
        input.Gamepad(true, Axes(leftX: 0.1f, leftY: -0.1f), NoButtons);
        input.Roll();
        Assert.Equal(Vector2.Zero, input.Stick(GamepadStick.Left));
    }

    [Fact]
    public void A_stick_is_rescaled_past_the_dead_zone_with_forward_positive()
    {
        var input = Create();
        input.Gamepad(true, Axes(leftY: -1f), NoButtons);
        input.Roll();
        Assert.Equal(new Vector2(0f, 1f), input.Stick(GamepadStick.Left));

        input.Gamepad(true, Axes(leftX: 0.6f), NoButtons);
        input.Roll();
        Assert.Equal(0.5f, input.Stick(GamepadStick.Left).X, 1e-5f);
        Assert.Equal(0f, input.Stick(GamepadStick.Left).Y, 1e-5f);
    }

    [Fact]
    public void Triggers_rest_at_zero_before_any_gamepad_is_seen()
    {
        var input = new Input { Readable = true };
        input.Roll();
        Assert.Equal(0f, input.Trigger(GamepadTrigger.Left));
        Assert.Equal(0f, input.Trigger(GamepadTrigger.Right));
    }

    [Fact]
    public void Two_taps_in_back_to_back_ticks_each_press_and_release()
    {
        var input = new Input { Readable = true };
        input.KeyDown(Key.Space);
        input.KeyUp(Key.Space);
        input.Roll();
        Assert.True(input.Pressed(Key.Space));
        Assert.False(input.Released(Key.Space));

        input.KeyDown(Key.Space);
        input.KeyUp(Key.Space);
        input.Roll();
        Assert.True(input.Pressed(Key.Space));
        Assert.True(input.Released(Key.Space));

        input.Roll();
        Assert.False(input.Pressed(Key.Space));
        Assert.True(input.Released(Key.Space));
    }

    [Fact]
    public void A_tap_followed_by_a_hold_still_releases_the_tap()
    {
        var input = new Input { Readable = true };
        input.KeyDown(Key.Space);
        input.KeyUp(Key.Space);
        input.Roll();

        input.KeyDown(Key.Space);
        input.Roll();
        Assert.True(input.Pressed(Key.Space));
        Assert.True(input.Released(Key.Space));
        Assert.True(input.Down(Key.Space));
    }

    [Fact]
    public void Triggers_map_to_zero_to_one()
    {
        var input = Create();
        input.Gamepad(true, Axes(leftTrigger: -1f, rightTrigger: 1f), NoButtons);
        input.Roll();
        Assert.Equal(0f, input.Trigger(GamepadTrigger.Left));
        Assert.Equal(1f, input.Trigger(GamepadTrigger.Right));
    }

    [Fact]
    public void Gamepad_buttons_press_and_release_like_keys()
    {
        var input = Create();
        var buttons = new bool[15];
        buttons[(int)GamepadButton.South] = true;
        input.Gamepad(true, Axes(), buttons);
        input.Roll();
        Assert.True(input.Pressed(GamepadButton.South));
        Assert.True(input.Down(GamepadButton.South));

        input.Gamepad(true, Axes(), NoButtons);
        input.Roll();
        Assert.True(input.Released(GamepadButton.South));
        Assert.False(input.Down(GamepadButton.South));
    }

    [Fact]
    public void Unplugging_the_gamepad_releases_its_buttons_and_centres_its_sticks()
    {
        var input = Create();
        var buttons = new bool[15];
        buttons[(int)GamepadButton.South] = true;
        input.Gamepad(true, Axes(leftX: 1f), buttons);
        input.Roll();
        Assert.True(input.GamepadConnected);

        input.Gamepad(false, default, default);
        input.Roll();
        Assert.True(input.Released(GamepadButton.South));
        Assert.False(input.GamepadConnected);
        Assert.Equal(Vector2.Zero, input.Stick(GamepadStick.Left));
    }

    [Fact]
    public void Reading_when_not_readable_throws()
    {
        var input = Create();
        input.Readable = false;
        var error = Assert.Throws<InvalidOperationException>(() => input.Down(Key.W));
        Assert.Contains("Update", error.Message);
    }

    [Fact]
    public void Listeners_hear_every_roll()
    {
        var input = Create();
        var listener = new CountingListener();
        input.Listen(listener);
        input.Roll();
        input.Roll();
        Assert.Equal(2, listener.Rolls);
        Assert.Equal(2, input.RollCount);
    }

    private sealed class CountingListener : IInputListener
    {
        public int Rolls { get; private set; }

        public void Rolled() => Rolls++;
    }
}
