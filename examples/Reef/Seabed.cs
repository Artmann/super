using System.Numerics;
using Rendition;
using Super;

namespace Reef;

/// <summary>The rolling sand. Fish and coral ask it for the ground's height.</summary>
internal static class Seabed
{
    private const float Size = 320f;
    private const int Quads = 220;

    /// <summary>No point on the seabed is higher than this, the sum of the waves in <see cref="Height"/>.</summary>
    public const float Highest = 3.6f;

    public static float Height(float x, float z) =>
        2.2f * MathF.Sin(x * 0.045f + 1f) * MathF.Cos(z * 0.038f)
        + 1.1f * MathF.Sin(x * 0.11f + z * 0.07f)
        + 0.3f * MathF.Sin(x * 0.31f - z * 0.27f);

    /// <summary>The seabed's mesh, with UVs laid so <paramref name="sand"/> repeats every <see cref="Surface.TileWidth"/> metres.</summary>
    public static GeneratedMesh CreateMesh(Surface sand) =>
        new(Vertices(1f / (sand.TileWidth ?? 1f)), Indices(), sand);

    private static Vertex[] Vertices(float uvPerMetre)
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
                    new Vector2(x, z) * uvPerMetre);
            }
        }

        return vertices;
    }

    private static uint[] Indices()
    {
        int side = Quads + 1;
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

        return indices;
    }
}
