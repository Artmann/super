using System.Numerics;

namespace Super.Tests;

/// <summary>Which device the player is on, and whose gamepad it is.</summary>
public sealed class DeviceTests
{
    private static readonly bool[] NoButtons = new bool[15];

    private static Input Create() => new() { Readable = true };

    private static float[] Axes(float leftX = 0f, float rightTrigger = -1f) => [leftX, 0f, 0f, 0f, -1f, rightTrigger];

    private static bool[] Holding(GamepadButton button)
    {
        bool[] buttons = new bool[15];
        buttons[(int)button] = true;
        return buttons;
    }

    [Fact]
    public void The_keyboard_and_mouse_are_the_device_until_another_is_used()
    {
        var input = Create();
        input.Roll();

        Assert.Equal(InputDevice.KeyboardAndMouse, input.Device);
    }

    [Fact]
    public void A_gamepad_button_makes_the_gamepad_the_device_and_a_key_takes_it_back()
    {
        var input = Create();
        input.Gamepad.Polled(true, Axes(), Holding(GamepadButton.South));
        input.Roll();
        Assert.Equal(InputDevice.Gamepad, input.Device);

        input.KeyDown(Key.Space);
        input.Roll();
        Assert.Equal(InputDevice.KeyboardAndMouse, input.Device);
    }

    [Fact]
    public void A_button_still_held_on_the_pad_does_not_take_the_device_back_from_the_keyboard()
    {
        var input = Create();
        input.Gamepad.Polled(true, Axes(), Holding(GamepadButton.South));
        input.Roll();
        input.KeyDown(Key.Space);

        input.Gamepad.Polled(true, Axes(), Holding(GamepadButton.South));
        input.Roll();

        Assert.Equal(InputDevice.KeyboardAndMouse, input.Device);
    }

    [Theory]
    [InlineData(0.3f, -1f, InputDevice.KeyboardAndMouse)]
    [InlineData(0.7f, -1f, InputDevice.Gamepad)]
    [InlineData(0f, -0.4f, InputDevice.KeyboardAndMouse)]
    [InlineData(0f, 0.4f, InputDevice.Gamepad)]
    public void A_stick_or_trigger_counts_once_it_is_past_halfway(float stick, float trigger, InputDevice expected)
    {
        var input = Create();
        input.Gamepad.Polled(true, Axes(stick, trigger), NoButtons);
        input.Roll();

        Assert.Equal(expected, input.Device);
    }

    [Fact]
    public void A_nudge_of_the_mouse_keeps_the_gamepad_but_a_real_move_takes_it_back()
    {
        var input = Create();
        input.Mouse.Feed.MouseMoved(new Vector2(100f, 100f));
        input.Gamepad.Polled(true, Axes(), Holding(GamepadButton.South));
        input.Roll();

        input.Mouse.Feed.MouseMoved(new Vector2(102f, 101f));
        input.Roll();
        Assert.Equal(InputDevice.Gamepad, input.Device);

        input.Mouse.Feed.MouseMoved(new Vector2(130f, 101f));
        input.Roll();
        Assert.Equal(InputDevice.KeyboardAndMouse, input.Device);
    }

    [Fact]
    public void The_wheel_and_a_mouse_button_count_as_the_mouse()
    {
        var input = Create();
        input.Gamepad.Polled(true, Axes(), Holding(GamepadButton.South));
        input.Mouse.Feed.Scrolled(1f);
        input.Roll();
        Assert.Equal(InputDevice.KeyboardAndMouse, input.Device);

        input.Gamepad.Polled(true, Axes(), Holding(GamepadButton.East));
        input.Mouse.Feed.MouseButtonDown(MouseButton.Left);
        input.Roll();
        Assert.Equal(InputDevice.KeyboardAndMouse, input.Device);
    }

    [Fact]
    public void A_finger_makes_the_screen_the_device()
    {
        var input = Create();
        input.Touches.Feed.Down(0, new Vector2(10f, 10f));
        input.Roll();

        Assert.Equal(InputDevice.Touch, input.Device);
    }

    [Fact]
    public void The_device_is_read_only_in_update()
    {
        var input = Create();
        input.Readable = false;

        var error = Assert.Throws<InvalidOperationException>(() => input.Device);
        Assert.Contains("only be read in Update", error.Message);
    }

    [Theory]
    [InlineData("Xbox Controller", GamepadFamily.Xbox)]
    [InlineData("XInput Gamepad (GLFW)", GamepadFamily.Xbox)]
    [InlineData("PS5 Controller", GamepadFamily.PlayStation)]
    [InlineData("Sony DualShock 4", GamepadFamily.PlayStation)]
    [InlineData("Wireless Controller", GamepadFamily.PlayStation)]
    [InlineData("Nintendo Switch Pro Controller", GamepadFamily.Nintendo)]
    [InlineData("Joy-Con (L/R)", GamepadFamily.Nintendo)]
    [InlineData("8BitDo SN30", GamepadFamily.Other)]
    public void The_family_comes_from_the_pad_name(string name, GamepadFamily expected)
    {
        var input = Create();
        input.Gamepad.Polled(true, Axes(), NoButtons, name);
        input.Roll();

        Assert.Equal(expected, input.Gamepad.Family);
        Assert.Equal(name, input.Gamepad.Name);
    }

    [Fact]
    public void Unplugging_the_pad_forgets_its_name_and_family()
    {
        var input = Create();
        input.Gamepad.Polled(true, Axes(), NoButtons, "PS5 Controller");
        input.Roll();

        input.Gamepad.Polled(false, default, default);
        input.Roll();

        Assert.Null(input.Gamepad.Name);
        Assert.Equal(GamepadFamily.Other, input.Gamepad.Family);
    }
}
