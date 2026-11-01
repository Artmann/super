using System.Numerics;

namespace Super;

/// <summary>Turns what GLFW reports into what <see cref="Input"/> understands.</summary>
internal static class GlfwInput
{
    /// <summary>Maps a GLFW key code to a <see cref="Key"/>, or returns false for keys the enum doesn't name.</summary>
    public static bool TryMapKey(int glfwKey, out Key key)
    {
        key = (Key)glfwKey;
        return glfwKey >= 0 && Enum.IsDefined(key);
    }

    /// <summary>GLFW's joystick slots run from 0 to 15.</summary>
    public const int JoystickSlots = 16;

    /// <summary>The first joystick slot holding a gamepad, or -1 when none is. Slot 0 is often a wheel or a keyboard that shows up as a joystick.</summary>
    public static int FirstGamepad(Func<int, bool> isGamepad)
    {
        for (int slot = 0; slot < JoystickSlots; slot++)
        {
            if (isGamepad(slot))
            {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>Scales a cursor position from window coordinates to framebuffer pixels, which is where the interface lays itself out.</summary>
    public static Vector2 ToFramebuffer(double x, double y, int windowWidth, int windowHeight, int framebufferWidth, int framebufferHeight)
    {
        if (windowWidth == 0 || windowHeight == 0)
        {
            return new Vector2((float)x, (float)y);
        }

        return new Vector2(
            (float)(x * framebufferWidth / windowWidth),
            (float)(y * framebufferHeight / windowHeight));
    }
}
