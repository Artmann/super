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

    /// <summary>
    /// Reads a one-channel image at (u, v) like <see cref="Bilinear"/>, but holds the edge pixels past the edges
    /// instead of wrapping, so a read near one edge never takes in the other.
    /// </summary>
    public static float BilinearClamped(float[] image, int size, float u, float v)
    {
        var fx = u * size - 0.5f;
        var fy = v * size - 0.5f;
        var x0 = (int)MathF.Floor(fx);
        var y0 = (int)MathF.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;
        var x1 = Math.Clamp(x0 + 1, 0, size - 1);
        var y1 = Math.Clamp(y0 + 1, 0, size - 1);

        x0 = Math.Clamp(x0, 0, size - 1);
        y0 = Math.Clamp(y0, 0, size - 1);

        var top = image[y0 * size + x0] + (image[y0 * size + x1] - image[y0 * size + x0]) * tx;
        var bottom = image[y1 * size + x0] + (image[y1 * size + x1] - image[y1 * size + x0]) * tx;

        return top + (bottom - top) * ty;
    }
}
