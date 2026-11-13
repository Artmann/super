using Silk.NET.GLFW;

namespace Super;

/// <summary>
/// The window's system cursor: hidden or locked as the game asks, and drawn with the game's own picture
/// (<see cref="Input.Cursor"/>) when it has one.
/// </summary>
internal sealed unsafe class WindowCursor
{
    private CursorModeValue _mode = CursorModeValue.CursorNormal;
    private CursorLook? _look;
    private float _scale;
    private Cursor* _cursor;

    /// <summary>Makes the window's cursor what <paramref name="input"/> asks for, at the interface's scale.</summary>
    public void Apply(Glfw glfw, WindowHandle* window, Input input, float scale)
    {
        ApplyMode(glfw, window, input);
        ApplyLook(glfw, window, input.Cursor, scale);
    }

    /// <summary>Gives the window the system's arrow back and frees the game's picture.</summary>
    public void Release(Glfw glfw, WindowHandle* window)
    {
        ApplyLook(glfw, window, null, 1f);
    }

    /// <summary>
    /// Hides and locks the cursor while the game captures the mouse, hides it while the game draws its own,
    /// and gives it back when it stops.
    /// </summary>
    private void ApplyMode(Glfw glfw, WindowHandle* window, Input input)
    {
        var mode = input.CaptureMouse ? CursorModeValue.CursorDisabled
            : input.HideCursor ? CursorModeValue.CursorHidden
            : CursorModeValue.CursorNormal;
        if (mode == _mode)
        {
            return;
        }

        _mode = mode;
        glfw.SetInputMode(window, CursorStateAttribute.Cursor, mode);

        // A captured mouse is for looking around, so read the mouse itself rather than the
        // pointer: the pointer has the system's acceleration on it, which makes turning
        // depend on how fast the mouse moved as well as how far.
        if (glfw.RawMouseMotionSupported())
        {
            glfw.SetInputMode(window, CursorStateAttribute.RawMouseMotion, mode == CursorModeValue.CursorDisabled);
        }
    }

    /// <summary>Makes the game's cursor picture the window's cursor, scaled as the interface is, when it changes.</summary>
    private void ApplyLook(Glfw glfw, WindowHandle* window, CursorLook? look, float scale)
    {
        if (look == _look && scale == _scale)
        {
            return;
        }

        _look = look;
        _scale = scale;
        Cursor* old = _cursor;
        _cursor = null;
        if (look is not null)
        {
            int size = Math.Max(1, (int)MathF.Round(look.Size * scale));
            byte[] pixels = Resample(look.Image.ReadPixels(), look.Image.Width, look.Image.Height, size);
            fixed (byte* data = pixels)
            {
                var image = new Image { Width = size, Height = size, Pixels = data };
                int hotX = Math.Clamp((int)MathF.Round(look.Hotspot.X * size / look.Image.Width), 0, size - 1);
                int hotY = Math.Clamp((int)MathF.Round(look.Hotspot.Y * size / look.Image.Height), 0, size - 1);
                _cursor = glfw.CreateCursor(&image, hotX, hotY);
            }
        }

        glfw.SetCursor(window, _cursor);
        if (old is not null)
        {
            glfw.DestroyCursor(old);
        }
    }

    /// <summary>
    /// Scales straight-alpha pixels to a square of <paramref name="size"/>, each target pixel the
    /// alpha-weighted mean of the source pixels it covers, so a shrunk picture keeps clean edges.
    /// </summary>
    private static byte[] Resample(byte[] source, int width, int height, int size)
    {
        var result = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            int top = y * height / size, bottom = Math.Max(top + 1, (y + 1) * height / size);
            for (int x = 0; x < size; x++)
            {
                int left = x * width / size, right = Math.Max(left + 1, (x + 1) * width / size);
                double r = 0, g = 0, b = 0, a = 0;
                int count = 0;
                for (int sy = top; sy < bottom && sy < height; sy++)
                {
                    for (int sx = left; sx < right && sx < width; sx++)
                    {
                        int i = (sy * width + sx) * 4;
                        double alpha = source[i + 3];
                        r += source[i] * alpha;
                        g += source[i + 1] * alpha;
                        b += source[i + 2] * alpha;
                        a += alpha;
                        count++;
                    }
                }

                int o = (y * size + x) * 4;
                if (a > 0)
                {
                    result[o] = (byte)Math.Round(r / a);
                    result[o + 1] = (byte)Math.Round(g / a);
                    result[o + 2] = (byte)Math.Round(b / a);
                    result[o + 3] = (byte)Math.Round(a / Math.Max(1, count));
                }
            }
        }

        return result;
    }
}
