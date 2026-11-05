using System.Numerics;
using Super;

namespace BricksExample;

/// <summary>
/// Textures for a laid surface, made in code: a colour, a height map and the normal map
/// that height gives, all tiling. Each stone is a rounded block sunk in mortar.
/// </summary>
public sealed class Masonry
{
    public required GeneratedTexture Color { get; init; }
    public required GeneratedTexture Height { get; init; }
    public required GeneratedTexture Normal { get; init; }

    /// <summary>Which stone a pixel is on, and how far it is from the stone's edge in pixels; -1 for mortar.</summary>
    private delegate (int Stone, float Inside) Layout(int x, int y);

    /// <summary>Bricks in running bond: each row moves half a brick along. A texture holds rows by columns of them.</summary>
    public static Masonry Bricks(int size, int columns, int rows, float metresAcross, float depth)
    {
        int width = size / columns;
        int height = size / rows;
        int mortar = Math.Max(size / 96, 2);
        (int, float) Brick(int x, int y)
        {
            int row = y / height;
            int shifted = (x + (row % 2) * width / 2) % size;
            int column = shifted / width;
            float inside = Math.Min(Math.Min(shifted % width, width - 1 - shifted % width), Math.Min(y % height, height - 1 - y % height)) - mortar * 0.5f;
            return (row * columns + column, inside);
        }

        return Make(size, Brick, metresAcross, depth, stone: new Vector3(0.55f, 0.2f, 0.13f), joint: new Vector3(0.62f, 0.6f, 0.55f), seed: 1);
    }

    /// <summary>Square setts in rows, each a little off square, as on an old street.</summary>
    public static Masonry Setts(int size, int across, float metresAcross, float depth)
    {
        int cell = size / across;
        int gap = Math.Max(size / 64, 3);
        (int, float) Sett(int x, int y)
        {
            int column = x / cell;
            int row = y / cell;
            int id = row * across + column;
            // Each sett's edges sit at their own spot, so the joints wander.
            float left = gap * (0.5f + Noise.Hash(id, 11));
            float right = gap * (0.5f + Noise.Hash(id, 12));
            float top = gap * (0.5f + Noise.Hash(id, 13));
            float bottom = gap * (0.5f + Noise.Hash(id, 14));
            float inside = Math.Min(Math.Min(x % cell - left, cell - 1 - x % cell - right), Math.Min(y % cell - top, cell - 1 - y % cell - bottom));
            return (id, inside);
        }

        return Make(size, Sett, metresAcross, depth, stone: new Vector3(0.26f, 0.25f, 0.24f), joint: new Vector3(0.3f, 0.26f, 0.2f), seed: 2);
    }

    /// <summary>
    /// Fine grit to lay over a large surface as detail textures: a colour around mid grey,
    /// which leaves the surface's own colour as it is on average, and its normal map.
    /// </summary>
    public static (GeneratedTexture Color, GeneratedTexture Normal) Grit(int size)
    {
        var heights = new float[size * size];
        var color = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float grain = Noise.Tiled(x, y, size, 8f, 3) * 0.6f + Noise.Tiled(x, y, size, 32f, 1) * 0.4f;
                heights[y * size + x] = grain;
                byte shade = (byte)Math.Clamp(128 + (grain - 0.5f) * 90f, 0f, 255f);
                Put(color, y * size + x, shade, shade, shade);
            }
        }

        // The grit stands about a millimetre high on a texture a quarter of a metre wide.
        return (new GeneratedTexture(size, color), NormalMap(heights, size, 0.001f / (0.25f / size)));
    }

    private static Masonry Make(int size, Layout layout, float metresAcross, float depth, Vector3 stone, Vector3 joint, int seed)
    {
        // A stone's edge rounds over this many pixels before it reaches its top.
        float bevel = size / 64f;
        var heights = new float[size * size];
        var color = new byte[size * size * 4];
        var heightPixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                var (id, inside) = layout(x, y);
                float rough = Noise.Tiled(x, y, size, 16f, seed);
                float h;
                Vector3 shade;
                if (inside <= 0f)
                {
                    h = 0.1f + 0.1f * rough;
                    shade = joint * (0.85f + 0.3f * rough);
                }
                else
                {
                    float rise = MathF.Min(inside / bevel, 1f);
                    rise = MathF.Sqrt(rise * (2f - rise));
                    h = 0.25f + 0.75f * rise * (0.85f + 0.15f * rough);
                    float tone = 0.8f + 0.4f * Noise.Hash(id, seed);
                    shade = stone * tone * (0.88f + 0.24f * rough);
                }

                heights[y * size + x] = h;
                byte value = (byte)MathF.Round(h * 255f);
                Put(heightPixels, y * size + x, value, value, value);
                Put(color, y * size + x, ToSrgb(shade.X), ToSrgb(shade.Y), ToSrgb(shade.Z));
            }
        }

        float metresPerPixel = metresAcross / size;
        return new Masonry
        {
            Color = new GeneratedTexture(size, color),
            Height = new GeneratedTexture(size, heightPixels),
            Normal = NormalMap(heights, size, depth / metresPerPixel),
        };
    }

    /// <summary>
    /// The normal map of a tiling height field. Strength is how many pixels across one
    /// unit of height rises. Green points up the picture, towards row 0.
    /// </summary>
    private static GeneratedTexture NormalMap(float[] heights, int size, float strength)
    {
        var pixels = new byte[size * size * 4];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float right = heights[y * size + (x + 1) % size] - heights[y * size + (x + size - 1) % size];
                float down = heights[(y + 1) % size * size + x] - heights[(y + size - 1) % size * size + x];
                var normal = Vector3.Normalize(new Vector3(-right * 0.5f * strength, down * 0.5f * strength, 1f));
                Put(pixels, y * size + x, Unit(normal.X), Unit(normal.Y), Unit(normal.Z));
            }
        }

        return new GeneratedTexture(size, pixels);
    }

    private static byte Unit(float value) => (byte)MathF.Round((value * 0.5f + 0.5f) * 255f);

    private static byte ToSrgb(float linear) => (byte)MathF.Round(MathF.Pow(Math.Clamp(linear, 0f, 1f), 1f / 2.2f) * 255f);

    private static void Put(byte[] pixels, int pixel, byte r, byte g, byte b)
    {
        pixels[pixel * 4] = r;
        pixels[pixel * 4 + 1] = g;
        pixels[pixel * 4 + 2] = b;
        pixels[pixel * 4 + 3] = 255;
    }
}

/// <summary>Smooth noise that tiles, from 0 to 1.</summary>
internal static class Noise
{
    public static float Hash(int value, int seed)
    {
        uint h = (uint)value * 374761393u + (uint)seed * 668265263u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return ((h ^ (h >> 16)) & 0xFFFFFF) / (float)0xFFFFFF;
    }

    /// <summary>Value noise with this many cells across a texture of this size, so it wraps at its edges.</summary>
    public static float Tiled(int x, int y, int size, float cells, int seed)
    {
        int count = (int)cells;
        float u = x * cells / size;
        float v = y * cells / size;
        int cx = (int)u;
        int cy = (int)v;
        float fx = Smooth(u - cx);
        float fy = Smooth(v - cy);
        float Corner(int i, int j) => Hash((cy + j) % count * count + (cx + i) % count, seed);
        float top = float.Lerp(Corner(0, 0), Corner(1, 0), fx);
        float bottom = float.Lerp(Corner(0, 1), Corner(1, 1), fx);
        return float.Lerp(top, bottom, fy);
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);
}
