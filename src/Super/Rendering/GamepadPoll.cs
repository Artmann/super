using Silk.NET.GLFW;

namespace Super;

/// <summary>Reads the first gamepad, if one is plugged in, so a tick sees it as it is now.</summary>
internal sealed class GamepadPoll(Glfw glfw)
{
    private readonly float[] _axes = new float[Input.GamepadAxisCount];
    private readonly bool[] _buttons = new bool[Input.GamepadButtonCount];
    private readonly Func<int, bool> _isGamepad = slot => glfw.JoystickIsGamepad(slot);

    /// <summary>Hands <paramref name="input"/> the first gamepad's sticks and buttons, or says there's none.</summary>
    public unsafe void Poll(Input input)
    {
        int slot = GlfwInput.FirstGamepad(_isGamepad);
        if (slot >= 0 && glfw.GetGamepadState(slot, out GamepadState state))
        {
            for (int i = 0; i < Input.GamepadAxisCount; i++)
            {
                _axes[i] = state.Axes[i];
            }

            for (int i = 0; i < Input.GamepadButtonCount; i++)
            {
                _buttons[i] = state.Buttons[i] != 0;
            }

            input.Gamepad.Polled(true, _axes, _buttons);
        }
        else
        {
            input.Gamepad.Polled(false, default, default);
        }
    }
}
