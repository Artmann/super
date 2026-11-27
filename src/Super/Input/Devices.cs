namespace Super;

/// <summary>What the player last played with, so prompts can show the buttons in their hand.</summary>
public enum InputDevice
{
    /// <summary>A key, a mouse button, the wheel, or the mouse moving more than a nudge.</summary>
    KeyboardAndMouse,

    /// <summary>A gamepad button, or a stick or trigger pushed past halfway.</summary>
    Gamepad,

    /// <summary>A finger on the screen.</summary>
    Touch,
}

/// <summary>Whose gamepad it is, which decides what its buttons are called and how they're drawn.</summary>
public enum GamepadFamily
{
    /// <summary>A, B, X and Y in green, red, blue and yellow.</summary>
    Xbox,

    /// <summary>A cross, a circle, a square and a triangle.</summary>
    PlayStation,

    /// <summary>B at the bottom and A on the right, the other way round from Xbox.</summary>
    Nintendo,

    /// <summary>A pad the name doesn't give away, shown with plain letters.</summary>
    Other,
}

/// <summary>Works out the device in use from what the window feeds, between ticks.</summary>
internal static class Devices
{
    // How far the mouse must move at once, in pixels, before it counts as picking up the mouse.
    private const float MouseNudge = 4f;

    /// <summary>Whether a move this far is the player picking up the mouse, and not the desk shaking.</summary>
    public static bool Moved(System.Numerics.Vector2 delta) => delta.LengthSquared() > MouseNudge * MouseNudge;

    /// <summary>
    /// Whether a stick is past halfway, or a trigger pulled past half, which GLFW reports above 0. The axes come in
    /// GLFW's order: the two sticks' x and y, then the two triggers.
    /// </summary>
    public static bool Pushed(ReadOnlySpan<float> axes)
    {
        for (int i = 0; i < axes.Length; i++)
        {
            if (i >= 4 ? axes[i] > 0f : MathF.Abs(axes[i]) > 0.5f)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The family a pad's name gives away, as GLFW and SDL report it: "Xbox Controller",
    /// "PS5 Controller", "Nintendo Switch Pro Controller" and so on.
    /// </summary>
    public static GamepadFamily FamilyOf(string? name)
    {
        string lower = (name ?? "").ToLowerInvariant();
        if (Mentions(lower, "xbox", "xinput", "microsoft"))
        {
            return GamepadFamily.Xbox;
        }

        if (Mentions(lower, "playstation", "ps3", "ps4", "ps5", "dualshock", "dualsense", "sony", "wireless controller"))
        {
            return GamepadFamily.PlayStation;
        }

        if (Mentions(lower, "nintendo", "switch", "joy-con", "pro controller"))
        {
            return GamepadFamily.Nintendo;
        }

        return GamepadFamily.Other;
    }

    private static bool Mentions(string name, params string[] words) => words.Any(name.Contains);
}
