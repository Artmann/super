namespace Super;

/// <summary>What a game window is opened with: its title and size, and how it shows.</summary>
/// <param name="Title">The window's title.</param>
/// <param name="Width">The window's width, in the screen's units.</param>
/// <param name="Height">The window's height, in the screen's units.</param>
internal sealed record WindowSetup(string Title, int Width, int Height)
{
    /// <summary>For a screenshot, a recording, a profile or a bake: nobody sees it, and it keeps the size asked for.</summary>
    public bool Hidden { get; init; }

    /// <summary>Whether the renderer runs under the Vulkan validation layers.</summary>
    public bool Validate { get; init; }

    /// <summary>Opens the window filling the screen. A hidden window ignores it.</summary>
    public bool Maximized { get; init; }

    /// <summary>Opens the window with no title bar or border. A hidden window ignores it.</summary>
    public bool Borderless { get; init; }

    /// <summary>Draws without tracing rays, as on a GPU that can't, as <c>--no-trace</c> asks.</summary>
    public bool NoRayTracing { get; init; }
}
