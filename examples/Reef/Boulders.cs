using System.Numerics;
using Rendition;
using Super;

namespace Reef;

/// <summary>Lumps of old reef rock and domes of brain coral, made by pushing a dome in and out by a few waves round it.</summary>
internal static class Boulders
{
    private const int Waves = 5;

    /// <summary>How wide a boulder is usually drawn, in metres. The rock tiles at that size, since the transform scales it.</summary>
    public const float TypicalWidth = 3f;

    /// <summary>
    /// A lump a metre across, flat underneath at zero, its surface tiled for drawing it <paramref name="typicalWidth"/>
    /// metres across. A <paramref name="lumpiness"/> of 1 is a rough boulder, and less is rounder.
    /// </summary>
    public static GeneratedMesh Create(Surface rock, RandomStream random, float typicalWidth = TypicalWidth, float lumpiness = 1f)
    {
        float uvPerMetre = typicalWidth / (rock.TileWidth ?? 1f);
        var dome = Shapes.Hemisphere(radius: 0.5f, segments: 40, rings: 14, surface: rock, uvPerMetre: uvPerMetre);
        var waves = new (Vector3 Direction, float Frequency, float Amount, float Phase)[Waves];
        for (int i = 0; i < Waves; i++)
        {
            waves[i] = (random.Direction3D(), random.Between(1.5f, 4.5f), random.Between(0.04f, 0.12f) / (1f + i * 0.4f) * lumpiness, random.Angle());
        }

        var vertices = dome.Vertices.ToArray();
        foreach (ref var vertex in vertices.AsSpan())
        {
            var position = vertex.Position;
            if (position.Y <= 0f)
            {
                // The base stays flat on the sand, pushed out as far as the rim above it.
                var rim = new Vector3(position.X, 0f, position.Z);
                vertex.Position = rim.Length() > 0f ? rim * Push(Vector3.Normalize(rim), waves) : rim;
                continue;
            }

            vertex.Position = position * Push(Vector3.Normalize(position), waves);
        }

        MeshParts.Smooth(vertices, dome.Indices);
        return dome with { Vertices = vertices };
    }

    private static float Push(Vector3 direction, ReadOnlySpan<(Vector3 Direction, float Frequency, float Amount, float Phase)> waves)
    {
        float push = 1f;
        foreach (var wave in waves)
        {
            push += wave.Amount * MathF.Sin(Vector3.Dot(direction, wave.Direction) * wave.Frequency * MathF.PI + wave.Phase);
        }

        return push;
    }
}
