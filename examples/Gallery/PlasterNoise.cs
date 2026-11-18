using Super;

namespace Gallery;

/// <summary>The plaster's texture: a tileable light-grey noise.</summary>
internal static class PlasterNoise
{
    /// <summary>
    /// A tileable light-grey noise, for plaster: values from 1 - <paramref name="depth"/> up to 1,
    /// in blotches about <paramref name="size"/> / 8 pixels across, with a finer layer on top. It
    /// wraps at the edges so it repeats without a seam.
    /// </summary>
    public static GeneratedTexture Texture(int size, float depth, int seed)
    {
        var random = new Random(seed);
        var coarse = Lattice(random, 8);
        var fine = Lattice(random, 32);
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float noise = Sample(coarse, 8, size, x, y) * 0.65f + Sample(fine, 32, size, x, y) * 0.35f;
                byte shade = (byte)Math.Clamp(MathF.Round(255f * (1f - depth * noise)), 0f, 255f);
                int at = (y * size + x) * 4;
                pixels[at] = shade;
                pixels[at + 1] = shade;
                pixels[at + 2] = shade;
                pixels[at + 3] = 255;
            }
        }

        return new GeneratedTexture(size, pixels);
    }

    /// <summary>A <paramref name="cells"/> by <paramref name="cells"/> grid of random values from 0 to 1.</summary>
    private static float[] Lattice(Random random, int cells)
    {
        var values = new float[cells * cells];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = (float)random.NextDouble();
        }

        return values;
    }

    /// <summary>The lattice smoothly blended at pixel (<paramref name="x"/>, <paramref name="y"/>) of an image <paramref name="size"/> wide, wrapping at the edges.</summary>
    private static float Sample(float[] lattice, int cells, int size, float x, float y)
    {
        float fx = x * cells / size;
        float fy = y * cells / size;
        int x0 = (int)fx;
        int y0 = (int)fy;
        float tx = fx - x0;
        float ty = fy - y0;
        tx = tx * tx * (3f - 2f * tx);
        ty = ty * ty * (3f - 2f * ty);
        float At(int cx, int cy) => lattice[(cy % cells) * cells + (cx % cells)];
        float top = At(x0, y0) + (At(x0 + 1, y0) - At(x0, y0)) * tx;
        float bottom = At(x0, y0 + 1) + (At(x0 + 1, y0 + 1) - At(x0, y0 + 1)) * tx;
        return top + (bottom - top) * ty;
    }
}
