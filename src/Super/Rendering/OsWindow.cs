using System.Runtime.InteropServices;
using Rendition;
using Silk.NET.Core.Native;
using Silk.NET.GLFW;

namespace Super;

/// <summary>The operating system's window, made with GLFW, which the renderer draws into.</summary>
internal sealed unsafe class OsWindow : IDisposable
{
    public OsWindow(WindowSetup setup)
    {
        bool hidden = setup.Hidden;
        GlfwApi = Glfw.GetApi();
        StartGlfw(GlfwApi, hidden);

        // Windows shrinks a window whose title bar would push it past the screen's height,
        // and a hidden window should come out at exactly the size that was asked for.
        // A window with no screen to fill (a screenshot, a recording) keeps the size that was asked for, so it ignores both options.
        bool maximized = setup.Maximized && !hidden;
        bool borderless = setup.Borderless && !hidden;
        GlfwApi.WindowHint(WindowHintBool.Decorated, !hidden && !borderless);
        GlfwApi.WindowHint(WindowHintBool.Maximized, maximized);
        NativeWindow = GlfwApi.CreateWindow(setup.Width, setup.Height, setup.Title, null, null);
        if (NativeWindow is null)
        {
            GlfwApi.Terminate();
            throw new InvalidOperationException("Could not create a window.");
        }

        if (!hidden)
        {
            // A maximized window is already in its place, and centring it would pull it off the corner.
            if (!maximized)
            {
                CentreOnScreen();
            }

            GlfwApi.ShowWindow(NativeWindow);
        }
    }

    public Glfw GlfwApi { get; }

    public WindowHandle* NativeWindow { get; }

    /// <summary>Whether the player asked to close the window.</summary>
    public bool ShouldClose => GlfwApi.WindowShouldClose(NativeWindow);

    /// <summary>The window's size in pixels, which is what the renderer draws.</summary>
    public (int Width, int Height) FramebufferSize()
    {
        GlfwApi.GetFramebufferSize(NativeWindow, out int width, out int height);
        return (width, height);
    }

    /// <summary>
    /// How many pixels the window has for each unit of its size: 2 on a Mac's Retina screen, where the window is sized
    /// in points, and 1 elsewhere, and in a hidden window, which asks for no Retina pixels.
    /// </summary>
    public float PixelsPerPoint
    {
        get
        {
            GlfwApi.GetWindowSize(NativeWindow, out int windowWidth, out _);
            GlfwApi.GetFramebufferSize(NativeWindow, out int framebufferWidth, out _);
            return windowWidth > 0 && framebufferWidth > 0 ? (float)framebufferWidth / windowWidth : 1f;
        }
    }

    /// <summary>Hands the operating system's waiting events to the callbacks.</summary>
    public void PollEvents() => GlfwApi.PollEvents();

    /// <summary>
    /// Starts GLFW on the Vulkan the renderer uses, and sets the hints every window shares: no OpenGL, and hidden, so a
    /// shown one appears already in its place rather than jumping there.
    /// </summary>
    private static void StartGlfw(Glfw glfw, bool hidden)
    {
        glfw.Context.TryGetProcAddress("glfwInitVulkanLoader", out nint initVulkanLoader);
        VulkanLibrary.ShareWithGlfw(initVulkanLoader);
        if (!glfw.Init())
        {
            throw new InvalidOperationException("Could not initialize GLFW.");
        }

        if (!glfw.VulkanSupported())
        {
            glfw.Terminate();
            throw new InvalidOperationException("GLFW found no Vulkan, so it can't make a window to draw in.");
        }

        glfw.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
        glfw.WindowHint(WindowHintBool.Visible, false);
        if (hidden && OperatingSystem.IsMacOS())
        {
            // A Retina screen would double a window's pixels, and a picture should be exactly the size asked for.
            const int cocoaRetinaFramebuffer = 0x00023001;
            glfw.WindowHint((WindowHintBool)cocoaRetinaFramebuffer, false);
        }
    }

    // Left to itself Windows cascades new windows down the screen. This centres the window, title bar
    // and all, in the primary monitor's work area, and keeps the title bar on screen if it doesn't fit.
    private void CentreOnScreen()
    {
        var monitor = GlfwApi.GetPrimaryMonitor();
        if (monitor is null)
        {
            return;
        }

        GlfwApi.GetMonitorWorkarea(monitor, out int areaX, out int areaY, out int areaWidth, out int areaHeight);
        GlfwApi.GetWindowFrameSize(NativeWindow, out int left, out int top, out int right, out int bottom);
        GlfwApi.GetWindowSize(NativeWindow, out int width, out int height);
        int x = areaX + (areaWidth - (left + width + right)) / 2 + left;
        int y = areaY + Math.Max(0, (areaHeight - (top + height + bottom)) / 2) + top;
        GlfwApi.SetWindowPos(NativeWindow, x, y);
    }

    /// <summary>Makes the Vulkan surface the renderer draws into, for the renderer's <paramref name="instance"/>.</summary>
    public ulong CreateSurface(nint instance)
    {
        VkNonDispatchableHandle surface;
        int result = GlfwApi.CreateWindowSurface(new VkHandle(instance), NativeWindow, null, &surface);
        return result == 0
            ? surface.Handle
            : throw new InvalidOperationException($"Could not create a window surface: {result}");
    }

    /// <summary>The Vulkan instance extensions GLFW needs to make a surface.</summary>
    public string[] InstanceExtensions()
    {
        byte** names = GlfwApi.GetRequiredInstanceExtensions(out uint count);
        var extensions = new string[count];
        for (int i = 0; i < count; i++)
        {
            extensions[i] = Marshal.PtrToStringUTF8((nint)names[i])!;
        }

        return extensions;
    }

    public void Dispose()
    {
        GlfwApi.DestroyWindow(NativeWindow);
        GlfwApi.Terminate();
    }
}
