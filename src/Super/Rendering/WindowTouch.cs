using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Silk.NET.GLFW;

namespace Super;

/// <summary>
/// Touch on Windows, which GLFW doesn't read. The window's message handler is swapped for one that takes the
/// <c>WM_POINTER</c> messages a touch screen sends and hands everything else on to GLFW's. Handling a touch
/// message keeps Windows from also turning it into a mouse click, so a finger never moves the cursor or hovers.
/// A pen and the mouse go on to GLFW as before.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class WindowTouch
{
    private const int WindowProcedure = -4;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmPointerUpdate = 0x0245;
    private const uint WmPointerDown = 0x0246;
    private const uint WmPointerUp = 0x0247;
    private const uint WmPointerCaptureChanged = 0x024C;
    private const uint TouchPointer = 2;
    private const uint InContact = 0x0004;
    private const uint Canceled = 0x8000;

    // Only the thread that pumps the window's messages reads it.
    private static readonly Dictionary<nint, Hooked> Windows = [];

    /// <summary>Starts reading the window's touches into this feed.</summary>
    public static void Attach(OsWindow window, TouchFeed feed)
    {
        if (new GlfwNativeWindow(window.GlfwApi, window.NativeWindow).Win32 is not { Hwnd: var handle })
        {
            return;
        }

        delegate* unmanaged<nint, uint, nint, nint, nint> procedure = &Procedure;
        nint previous = SetWindowLongPtrW(handle, WindowProcedure, (nint)procedure);
        Windows[handle] = new Hooked(previous, window, feed);
    }

    [UnmanagedCallersOnly]
    private static nint Procedure(nint handle, uint message, nint wParam, nint lParam)
    {
        if (!Windows.TryGetValue(handle, out var hooked))
        {
            return DefWindowProcW(handle, message, wParam, lParam);
        }

        try
        {
            if (message is WmPointerUpdate or WmPointerDown or WmPointerUp or WmPointerCaptureChanged && Touched(hooked, handle, message, wParam, lParam))
            {
                return 0;
            }
        }
        catch (Exception problem)
        {
            // An exception can't go back through Windows, so it's told and the message goes on as if untouched.
            Console.Error.WriteLine($"Reading a touch failed: {problem.Message}");
        }

        nint result = CallWindowProcW(hooked.Previous, handle, message, wParam, lParam);
        if (message == WmNcDestroy)
        {
            Windows.Remove(handle);
        }

        return result;
    }

    /// <summary>Feeds a touch message to the game. Returns whether it was a finger, which Windows then leaves alone.</summary>
    private static bool Touched(Hooked hooked, nint handle, uint message, nint wParam, nint lParam)
    {
        uint id = (uint)(wParam & 0xFFFF);
        if (!GetPointerType(id, out uint type) || type != TouchPointer)
        {
            return false;
        }

        uint flags = (uint)((wParam >> 16) & 0xFFFF);
        int finger = (int)id;
        switch (message)
        {
            case WmPointerDown:
                hooked.Feed.Down(finger, Where(hooked.Window, handle, lParam));
                break;
            case WmPointerUpdate when (flags & InContact) != 0:
                hooked.Feed.Moved(finger, Where(hooked.Window, handle, lParam));
                break;
            case WmPointerUp when (flags & Canceled) == 0:
                hooked.Feed.Up(finger);
                break;
            case WmPointerUp or WmPointerCaptureChanged:
                hooked.Feed.Cancelled(finger);
                break;
        }

        return true;
    }

    /// <summary>A touch's place on the screen, as the pixels the game draws.</summary>
    private static Vector2 Where(OsWindow window, nint handle, nint lParam)
    {
        var point = new Point((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));
        ScreenToClient(handle, ref point);
        var glfw = window.GlfwApi;
        glfw.GetWindowSize(window.NativeWindow, out int width, out int height);
        glfw.GetFramebufferSize(window.NativeWindow, out int framebufferWidth, out int framebufferHeight);
        return GlfwInput.ToFramebuffer(point.X, point.Y, (width, height), (framebufferWidth, framebufferHeight));
    }

    private sealed record Hooked(nint Previous, OsWindow Window, TouchFeed Feed);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint previous, nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPointerType(uint pointerId, out uint pointerType);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(nint window, ref Point point);
}
