using Silk.NET.GLFW;

namespace Super;

/// <summary>
/// Feeds the window's events into the game's input. Only the interactive loop listens. The stats panel's
/// <see cref="GameStats.ToggleKey"/> belongs to the engine, and the game never sees it.
/// </summary>
internal sealed unsafe class WindowInputFeed
{
    private readonly OsWindow _window;
    private readonly Input _input;
    private readonly GameStats _stats;

    // GLFW keeps only a pointer to each callback, so the delegates live here to stay alive.
    private readonly GlfwCallbacks.KeyCallback _onKey;
    private readonly GlfwCallbacks.MouseButtonCallback _onMouseButton;
    private readonly GlfwCallbacks.CursorPosCallback _onCursorPos;
    private readonly GlfwCallbacks.CursorEnterCallback _onCursorEnter;
    private readonly GlfwCallbacks.ScrollCallback _onScroll;
    private readonly GlfwCallbacks.CharCallback _onChar;

    public WindowInputFeed(OsWindow window, Input input, GameStats stats)
    {
        _window = window;
        _input = input;
        _stats = stats;
        _onKey = OnKey;
        _onMouseButton = OnMouseButton;
        _onCursorPos = OnCursorPos;
        _onCursorEnter = OnCursorEnter;
        _onScroll = (_, _, offsetY) => input.Mouse.Feed.Scrolled((float)offsetY);
        _onChar = OnChar;
    }

    /// <summary>Hands the window's events to the input from now on.</summary>
    public void Listen()
    {
        var glfw = _window.GlfwApi;
        var window = _window.NativeWindow;
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

    private void OnKey(WindowHandle* window, Silk.NET.GLFW.Keys key, int scanCode, InputAction action, KeyModifiers mods)
    {
        if (action == InputAction.Repeat || !GlfwInput.TryMapKey((int)key, out var mapped))
        {
            return;
        }

        if (_stats.TakeKey(mapped, action == InputAction.Press))
        {
            return;
        }

        if (action == InputAction.Press)
        {
            _input.KeyDown(mapped);
        }
        else
        {
            _input.KeyUp(mapped);
        }
    }

    private void OnMouseButton(WindowHandle* window, Silk.NET.GLFW.MouseButton button, InputAction action, KeyModifiers mods)
    {
        if ((int)button >= Input.MouseButtonCount)
        {
            return;
        }

        if (action == InputAction.Press)
        {
            _input.Mouse.Feed.MouseButtonDown((MouseButton)button);
        }
        else if (action == InputAction.Release)
        {
            _input.Mouse.Feed.MouseButtonUp((MouseButton)button);
        }
    }

    private void OnCursorPos(WindowHandle* window, double x, double y)
    {
        var glfw = _window.GlfwApi;
        glfw.GetWindowSize(_window.NativeWindow, out int windowWidth, out int windowHeight);
        glfw.GetFramebufferSize(_window.NativeWindow, out int framebufferWidth, out int framebufferHeight);
        _input.Mouse.Feed.MouseMoved(GlfwInput.ToFramebuffer(x, y, (windowWidth, windowHeight), (framebufferWidth, framebufferHeight)));
    }

    private void OnCursorEnter(WindowHandle* window, bool entered)
    {
        if (!entered)
        {
            _input.Mouse.Feed.MouseLeftWindow();
        }
    }

    private void OnChar(WindowHandle* window, uint codepoint)
    {
        foreach (char character in char.ConvertFromUtf32((int)codepoint))
        {
            _input.TypedCharacter(character);
        }
    }
}
