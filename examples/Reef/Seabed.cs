using System.Numerics;
using Rendition;
using Super;

namespace Reef;

/// <summary>The rolling sand. Fish and coral ask it for the ground's height.</summary>
internal static class Seabed
{
    private const float Size = 320f;
    private const int Quads = 220;
    private const int TextureSize = 512;
    private const float TextureRepeat = 0.25f; // Times per metre.

    /// <summary>No point on the seabed is higher than this, the sum of the waves in <see cref="Height"/>.</summary>
    public const float Highest = 3.6f;

    public static float Height(float x, float z) =>
        2.2f * MathF.Sin(x * 0.045f + 1f) * MathF.Cos(z * 0.038f)
        + 1.1f * MathF.Sin(x * 0.11f + z * 0.07f)
        + 0.3f * MathF.Sin(x * 0.31f - z * 0.27f);

    public static GeneratedMesh CreateMesh(RandomStream random)
    {
        const float Step = Size / Quads;
        const float Half = Size / 2f;
        int side = Quads + 1;
        var vertices = new Vertex[side * side];
        for (int row = 0; row < side; row++)
        {
            for (int column = 0; column < side; column++)
            {
                float x = column * Step - Half;
                float z = row * Step - Half;
                var normal = Vector3.Normalize(new Vector3(
                    Height(x - Step, z) - Height(x + Step, z), 2f * Step, Height(x, z - Step) - Height(x, z + Step)));
                vertices[row * side + column] = new Vertex(
                    new Vector3(x, Height(x, z), z),
                    normal,
                    new Vector4(1f, 0f, 0f, 1f),
                    new Vector2(x, z) * TextureRepeat);
            }
        }

        uint[] indices = new uint[Quads * Quads * 6];
        int next = 0;
        for (int row = 0; row < Quads; row++)
        {
            for (int column = 0; column < Quads; column++)
            {
                uint corner = (uint)(row * side + column);
                uint below = corner + (uint)side;
                indices[next++] = corner;
                indices[next++] = below;
                indices[next++] = corner + 1;
                indices[next++] = corner + 1;
                indices[next++] = below;
                indices[next++] = below + 1;
            }
        }

        return new GeneratedMesh(vertices, indices, CreateSand(random), 0.9f);
    }

    /// <summary>Pale grains over soft blotches.</summary>
    private static GeneratedTexture CreateSand(RandomStream random)
    {
        byte[] pixels = new byte[TextureSize * TextureSize * 4];
        for (int y = 0; y < TextureSize; y++)
        {
            for (int x = 0; x < TextureSize; x++)
            {
                float u = x * MathF.Tau / TextureSize;
                float v = y * MathF.Tau / TextureSize;
                float blotch = 0.5f + 0.25f * (MathF.Sin(u * 3f + MathF.Sin(v * 2f) * 2f) + MathF.Cos(v * 5f + MathF.Sin(u * 4f)));
                float shade = 0.72f + 0.16f * blotch + random.Between(-0.1f, 0.1f);
                int pixel = (y * TextureSize + x) * 4;
                pixels[pixel] = ToByte(shade);
                pixels[pixel + 1] = ToByte(shade * 0.93f);
                pixels[pixel + 2] = ToByte(shade * 0.78f);
                pixels[pixel + 3] = 255;
            }
        }

        return new GeneratedTexture(TextureSize, pixels);
    }

    private static byte ToByte(float value) => (byte)(Math.Clamp(value, 0f, 1f) * 255f);
}
