using System.Numerics;
using Rendition;
using Super;
using Super.Terrain;

namespace Hamlet;

/// <summary>
/// A village's ground: the hill in square chunks, and the paths as strips of packed earth
/// laid over it. Made again for every village, and unloaded when the next one comes.
/// </summary>
internal sealed class Ground
{
    private const int ChunkQuads = 32;
    private const float Spacing = 2f;
    private const float PathLift = 0.05f;

    private static readonly Surface Grass = new() { Color = new Vector3(0.24f, 0.33f, 0.15f), Roughness = 0.95f, WorldVariation = true };
    private static readonly Surface Earth = new() { Color = new Vector3(0.38f, 0.3f, 0.21f), Roughness = 0.95f, WorldVariation = true };

    private readonly List<ModelRef> _models = [];

    public Ground(Content content, Village village)
    {
        var field = new Heightfield(village.Height, Spacing);
        int chunks = (int)MathF.Ceiling(Village.GroundReach / (ChunkQuads * Spacing));
        for (int z = -chunks; z < chunks; z++)
        {
            for (int x = -chunks; x < chunks; x++)
            {
                _models.Add(content.Mesh(field.Chunk(x, z, ChunkQuads, Grass, 0.25f)));
            }
        }

        for (int i = 0; i < village.Paths.Count; i++)
        {
            // Each path a hair higher than the last, so crossings don't flicker.
            _models.Add(content.Mesh(Strip(village.Paths[i], field, PathLift + i * 0.006f)));
        }
    }

    public IReadOnlyList<ModelRef> Models => _models;

    public void Unload(Content content)
    {
        foreach (var model in _models)
        {
            content.Unload(model);
        }

        _models.Clear();
    }

    /// <summary>A strip of earth along a path, following the ground across its width as well as along it.</summary>
    private static GeneratedMesh Strip(Spline path, Heightfield field, float lift)
    {
        const int Across = 4;
        int along = (int)MathF.Ceiling(path.Length) + 1;
        var vertices = new Vertex[along * Across];
        for (int i = 0; i < along; i++)
        {
            float distance = MathF.Min(i, path.Length);
            var pose = path.FrameAtDistance(distance);
            var forward = Vector3.Normalize(new Vector3(pose.Forward.X, 0f, pose.Forward.Z));
            var right = new Vector3(forward.Z, 0f, -forward.X);
            for (int j = 0; j < Across; j++)
            {
                float offset = (j / (Across - 1f) * 2f - 1f) * Village.PathHalfWidth;
                var at = pose.Position + right * offset;
                at.Y = field.Height(at.X, at.Z) + lift;
                vertices[i * Across + j] = new Vertex(at, field.Normal(at.X, at.Z), new Vector4(forward, 1f), new Vector2(offset, distance));
            }
        }

        var indices = new List<uint>();
        for (int i = 0; i < along - 1; i++)
        {
            for (int j = 0; j < Across - 1; j++)
            {
                uint a = (uint)(i * Across + j);
                uint b = a + 1;
                uint c = a + Across;
                uint d = c + 1;
                Triangle(indices, vertices, a, c, b);
                Triangle(indices, vertices, b, c, d);
            }
        }

        return new GeneratedMesh(vertices, [.. indices], Earth, Simplify: false);
    }

    /// <summary>Adds a triangle wound so that it faces up.</summary>
    private static void Triangle(List<uint> indices, Vertex[] vertices, uint a, uint b, uint c)
    {
        var normal = Vector3.Cross(vertices[b].Position - vertices[a].Position, vertices[c].Position - vertices[a].Position);
        indices.Add(a);
        if (normal.Y >= 0f)
        {
            indices.Add(b);
            indices.Add(c);
        }
        else
        {
            indices.Add(c);
            indices.Add(b);
        }
    }
}
