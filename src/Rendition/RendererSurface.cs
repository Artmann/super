namespace Rendition;

/// <summary>
/// What the renderer draws into: a window, or nothing a window shows. It names the window, not the graphics API, so
/// the renderer picks how to reach it.
/// </summary>
public sealed class RendererSurface
{
    private RendererSurface(RendererSurfaceKind kind, nint window)
    {
        Kind = kind;
        GlfwWindow = window;
    }

    /// <summary>
    /// Draws into nothing a window shows. Each frame can still be read back with <see cref="Renderer.Capture"/>, which
    /// suits tests and machines without a display.
    /// </summary>
    public static RendererSurface Headless { get; } = new(RendererSurfaceKind.Headless, 0);

    internal RendererSurfaceKind Kind { get; }

    /// <summary>The GLFW window drawn into, for <see cref="RendererSurfaceKind.Glfw"/>.</summary>
    internal nint GlfwWindow { get; }

    /// <summary>
    /// Draws into a GLFW window, made with no client API. GLFW must have been started with the renderer's Vulkan loader,
    /// as Super's window does.
    /// </summary>
    public static RendererSurface Window(nint glfwWindow) =>
        glfwWindow != 0
            ? new RendererSurface(RendererSurfaceKind.Glfw, glfwWindow)
            : throw new ArgumentException("The GLFW window is null.", nameof(glfwWindow));
}

/// <summary>The kinds of <see cref="RendererSurface"/>.</summary>
internal enum RendererSurfaceKind
{
    Glfw,
    Headless,
}
