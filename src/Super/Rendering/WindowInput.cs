using Silk.NET.GLFW;

namespace Super;

/// <summary>
/// Feeds the window's events into the game's input, and reads the first gamepad. Only the interactive loop listens.
/// F3 belongs to the engine: it shows and hides the stats panel, and the game never sees it.
/// </summary>
internal sealed unsafe class WindowInput(Glfw glfw, WindowHandle* window)
{
    /// <summary>The key that shows and hides the engine's stats panel.</summary>
    internal const Key StatsKey = Key.F3;

    private readonly float[] _gamepadAxes = new float[Input.GamepadAxisCount];
    private readonly bool[] _gamepadButtons = new bool[Input.GamepadButtonCount];
    private readonly Func<int, bool> _isGamepad = slot => glfw.JoystickIsGamepad(slot);

    // GLFW keeps only a pointer to each callback, so the delegates live here to stay alive.
    private GlfwCallbacks.KeyCallback? _onKey;
    private GlfwCallbacks.MouseButtonCallback? _onMouseButton;
    private GlfwCallbacks.CursorPosCallback? _onCursorPos;
    private GlfwCallbacks.CursorEnterCallback? _onCursorEnter;
    private GlfwCallbacks.ScrollCallback? _onScroll;
    private GlfwCallbacks.CharCallback? _onChar;

    public void Listen(Input input, GameStats stats)
    {
        _onKey = (_, key, _, action, _) => OnKey(input, stats, key, action);
        _onMouseButton = (_, button, action, _) => OnButton(input, button, action);
        _onCursorPos = (_, x, y) =>
        {
            glfw.GetWindowSize(window, out int windowWidth, out int windowHeight);
            glfw.GetFramebufferSize(window, out int framebufferWidth, out int framebufferHeight);
            input.MouseMoved(GlfwInput.ToFramebuffer(x, y, windowWidth, windowHeight, framebufferWidth, framebufferHeight));
        };
        _onCursorEnter = (_, entered) =>
        {
            if (!entered)
            {
                input.MouseLeftWindow();
            }
        };
        _onScroll = (_, _, offsetY) => input.Scrolled((float)offsetY);
        _onChar = (_, codepoint) =>
        {
            foreach (char character in char.ConvertFromUtf32((int)codepoint))
            {
                input.TypedCharacter(character);
            }
        };

        glfw.SetKeyCallback(window, _onKey);
        glfw.SetMouseButtonCallback(window, _onMouseButton);
        glfw.SetCursorPosCallback(window, _onCursorPos);
        glfw.SetCursorEnterCallback(window, _onCursorEnter);
        glfw.SetScrollCallback(window, _onScroll);
        glfw.SetCharCallback(window, _onChar);

        // The cursor may already be over the window, and a first click shouldn't need a move.
        glfw.GetCursorPos(window, out double cursorX, out double cursorY);
        glfw.GetWindowSize(window, out int width, out int height);
        if (cursorX >= 0 && cursorY >= 0 && cursorX < width && cursorY < height)
        {
            _onCursorPos(window, cursorX, cursorY);
        }
    }

    /// <summary>Reads the first gamepad, if one is plugged in, so a tick sees it as it is now.</summary>
    public void PollGamepad(Input input)
    {
        int slot = GlfwInput.FirstGamepad(_isGamepad);
        if (slot >= 0 && glfw.GetGamepadState(slot, out GamepadState state))
        {
            for (int i = 0; i < Input.GamepadAxisCount; i++)
            {
                _gamepadAxes[i] = state.Axes[i];
            }

            for (int i = 0; i < Input.GamepadButtonCount; i++)
            {
                _gamepadButtons[i] = state.Buttons[i] != 0;
            }

            input.Gamepad(true, _gamepadAxes, _gamepadButtons);
        }
        else
        {
            input.Gamepad(false, default, default);
        }
    }

    private static void OnKey(Input input, GameStats stats, Silk.NET.GLFW.Keys key, InputAction action)
    {
        if (action == InputAction.Repeat || !GlfwInput.TryMapKey((int)key, out var mapped))
        {
            return;
        }

        if (mapped == StatsKey)
        {
            if (action == InputAction.Press)
            {
                stats.Shown = !stats.Shown;
            }

            return;
        }

        if (action == InputAction.Press)
        {
            input.KeyDown(mapped);
        }
        else
        {
            input.KeyUp(mapped);
        }
    }

    private static void OnButton(Input input, Silk.NET.GLFW.MouseButton button, InputAction action)
    {
        if ((int)button >= Input.MouseButtonCount)
        {
            return;
        }

        if (action == InputAction.Press)
        {
            input.MouseButtonDown((MouseButton)button);
        }
        else if (action == InputAction.Release)
        {
            input.MouseButtonUp((MouseButton)button);
        }
    }
}
