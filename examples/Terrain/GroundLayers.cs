using System.Numerics;
using Rendition;
using Super;

namespace Terrain;

/// <summary>
/// The ground's four layers, sand, grass, rock and snow, each a colour and a normal map, made in code and kept in two
/// texture arrays that the terrain shader picks between.
/// </summary>
internal sealed record GroundLayers(TextureArrayRef Colors, TextureArrayRef Normals)
{
    private const int LayerSize = 64;

    /// <summary>Makes the layers from <paramref name="looks"/>.</summary>
    public static GroundLayers Make(Content content, RandomStream looks)
    {
        TextureRef[] colorLayers =
        [
            content.Texture(ColorLayer(looks, new Vector3(0.66f, 0.58f, 0.40f), 0.05f)),
            content.Texture(ColorLayer(looks, new Vector3(0.28f, 0.46f, 0.17f), 0.08f)),
            content.Texture(ColorLayer(looks, new Vector3(0.44f, 0.42f, 0.40f), 0.14f)),
            content.Texture(ColorLayer(looks, new Vector3(0.93f, 0.95f, 0.98f), 0.02f)),
        ];
        TextureRef[] bumpLayers =
        [
            content.Texture(BumpLayer(looks, 0.015f, 3), TextureKind.Linear, normalMap: true),
            content.Texture(BumpLayer(looks, 0.02f, 7), TextureKind.Linear, normalMap: true),
            content.Texture(BumpLayer(looks, 0.12f, 2), TextureKind.Linear, normalMap: true),
            content.Texture(BumpLayer(looks, 0.015f, 4), TextureKind.Linear, normalMap: true),
        ];
        var layers = new GroundLayers(content.TextureArray(colorLayers), content.TextureArray(bumpLayers));

        // The arrays keep their own copy of each layer, so the single textures need not
        // take texture slots as well.
        foreach (var layer in colorLayers)
        {
            content.Unload(layer);
        }

        foreach (var layer in bumpLayers)
        {
            content.Unload(layer);
        }

        return layers;
    }

    private static GeneratedTexture ColorLayer(RandomStream stream, Vector3 color, float grain)
    {
        var mottle = Tileable(stream, 4);
        var pixels = new byte[LayerSize * LayerSize * 4];
        for (int i = 0; i < LayerSize * LayerSize; i++)
        {
            float shade = (0.92f + mottle[i] * 0.16f) * (1f + stream.Between(-grain, grain));
            pixels[i * 4] = Byte(color.X * shade);
            pixels[i * 4 + 1] = Byte(color.Y * shade);
            pixels[i * 4 + 2] = Byte(color.Z * shade);
            pixels[i * 4 + 3] = 255;
        }

        return new GeneratedTexture(LayerSize, pixels);
    }

    /// <summary>A normal map of gentle random bumps, so each layer catches light its own way.</summary>
    private static GeneratedTexture BumpLayer(RandomStream stream, float strength, int bumpsAcross)
    {
        var heights = Tileable(stream, bumpsAcross);
        var pixels = new byte[LayerSize * LayerSize * 4];
        for (int row = 0; row < LayerSize; row++)
        {
            for (int column = 0; column < LayerSize; column++)
            {
                float slopeX = heights[row * LayerSize + (column + 1) % LayerSize] - heights[row * LayerSize + (column + LayerSize - 1) % LayerSize];
                float slopeY = heights[((row + 1) % LayerSize) * LayerSize + column] - heights[((row + LayerSize - 1) % LayerSize) * LayerSize + column];
                var normal = Vector3.Normalize(new Vector3(-slopeX * strength * LayerSize, -slopeY * strength * LayerSize, 1f));
                int offset = (row * LayerSize + column) * 4;
                pixels[offset] = Byte(normal.X * 0.5f + 0.5f);
                pixels[offset + 1] = Byte(normal.Y * 0.5f + 0.5f);
                pixels[offset + 2] = Byte(normal.Z * 0.5f + 0.5f);
                pixels[offset + 3] = 255;
            }
        }

        return new GeneratedTexture(LayerSize, pixels);
    }

    /// <summary>
    /// Smooth random values over the texture that wrap at its edges, so the tiles meet
    /// without a seam. There are this many bumps across the texture.
    /// </summary>
    private static float[] Tileable(RandomStream stream, int cells)
    {
        var lattice = new float[cells * cells];
        for (int i = 0; i < lattice.Length; i++)
        {
            lattice[i] = stream.Float();
        }

        var values = new float[LayerSize * LayerSize];
        for (int row = 0; row < LayerSize; row++)
        {
            for (int column = 0; column < LayerSize; column++)
            {
                float x = column * cells / (float)LayerSize;
                float y = row * cells / (float)LayerSize;
                int cellX = (int)x;
                int cellY = (int)y;
                float fx = Smooth(x - cellX);
                float fy = Smooth(y - cellY);
                int nextX = (cellX + 1) % cells;
                int nextY = (cellY + 1) % cells;
                float top = Lerp(lattice[cellY * cells + cellX], lattice[cellY * cells + nextX], fx);
                float bottom = Lerp(lattice[nextY * cells + cellX], lattice[nextY * cells + nextX], fx);
                values[row * LayerSize + column] = Lerp(top, bottom, fy);
            }
        }

        return values;
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static byte Byte(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
}
