namespace Super.Materials;

internal static class Sampling
{
    /// <summary>Wraps <paramref name="i"/> into 0 up to <paramref name="size"/>.</summary>
    public static int Wrap(int i, int size)
    {
        var m = i % size;

        return m < 0 ? m + size : m;
    }

    /// <summary>
    /// Reads one channel of an interleaved image at (u, v), as fractions of the image,
    /// blending the four nearest pixels and wrapping at the edges.
    /// </summary>
    public static float Bilinear(float[] image, int size, int channels, int channel, float u, float v)
    {
        var fx = u * size - 0.5f;
        var fy = v * size - 0.5f;
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;
        var x1 = Wrap(x0 + 1, size);
        var y1 = Wrap(y0 + 1, size);

        x0 = Wrap(x0, size);
        y0 = Wrap(y0, size);

        float At(int x, int y) => image[(y * size + x) * channels + channel];

        var top = At(x0, y0) + (At(x1, y0) - At(x0, y0)) * tx;
        var bottom = At(x0, y1) + (At(x1, y1) - At(x0, y1)) * tx;

        return top + (bottom - top) * ty;
    }
}
