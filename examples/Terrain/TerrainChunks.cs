using System.Numerics;
using Rendition;
using Super;

namespace Terrain;

/// <summary>
/// The ground near the camera, in square chunks: each a mesh of the hills and a splat map of where grass grows. Chunks
/// that come near the camera are made as entities, and those it leaves well behind are unloaded.
/// </summary>
internal sealed class TerrainChunks(World world, Content content, Hills hills, Noise moisture, ShaderRef shader, GroundLayers layers)
{
    public const int ChunkQuads = 32;
    public const float ChunkSize = ChunkQuads * Hills.Spacing;
    public const int LoadRadius = 4;
    public const int UnloadRadius = LoadRadius + 1;

    private const float UvPerMetre = 0.125f;
    private const int SplatSize = 32;
    private const float SnowHeight = 26f;
    private const float SnowFade = 6f;
    private const float RockSteepness = 0.18f;
    private const float RockFade = 0.12f;

    private readonly Dictionary<(int X, int Z), Chunk> _chunks = [];
    private readonly List<(int X, int Z)> _leaving = [];

    /// <summary>Which chunks are loaded right now, by chunk coordinates.</summary>
    public IReadOnlyCollection<(int X, int Z)> Loaded => _chunks.Keys;

    /// <summary>Loads chunks that came near <paramref name="position"/> and unloads those it left well behind.</summary>
    public void Update(Vector3 position)
    {
        int centerX = (int)MathF.Floor(position.X / ChunkSize);
        int centerZ = (int)MathF.Floor(position.Z / ChunkSize);

        foreach (var key in _chunks.Keys)
        {
            if (Math.Max(Math.Abs(key.X - centerX), Math.Abs(key.Z - centerZ)) > UnloadRadius)
            {
                _leaving.Add(key);
            }
        }

        foreach (var key in _leaving)
        {
            var chunk = _chunks[key];
            _chunks.Remove(key);
            world.Destroy(chunk.Entity);
            content.Models.Unload(chunk.Model);
            content.Unload(chunk.Splat);
        }

        _leaving.Clear();

        for (int z = centerZ - LoadRadius; z <= centerZ + LoadRadius; z++)
        {
            for (int x = centerX - LoadRadius; x <= centerX + LoadRadius; x++)
            {
                if (!_chunks.ContainsKey((x, z)))
                {
                    _chunks.Add((x, z), LoadChunk(x, z));
                }
            }
        }
    }

    private Chunk LoadChunk(int chunkX, int chunkZ)
    {
        var splat = content.Texture(Splat(chunkX, chunkZ), TextureKind.Linear);
        var surface = new Surface
        {
            Color = Vector3.One,
            Roughness = 0.9f,
            Shader = new SurfaceShader(shader)
            {
                Parameters =
                [
                    new Vector4(SnowHeight, SnowFade, RockSteepness, RockFade),
                    new Vector4(chunkX * ChunkSize, chunkZ * ChunkSize, 1f / ChunkSize, SplatSize),
                ],
                Textures = [splat],
                Arrays = [layers.Colors, layers.Normals],
            },
        };
        var model = content.Mesh(hills.Field.Chunk(chunkX, chunkZ, ChunkQuads, surface, UvPerMetre));
        // The splat's edge texels sit on the chunk's edges, so its area reaches half a texel past them.
        float texel = ChunkSize / (SplatSize - 1f);
        var origin = new Vector2(chunkX, chunkZ) * ChunkSize;
        var grass = new Grass { Density = new GrassMap(splat, origin - new Vector2(texel / 2f), origin + new Vector2(ChunkSize + texel / 2f)) };
        return new Chunk(model, splat, world.Spawn(Transform.Identity, new Model(model), grass));
    }

    /// <summary>How grassy each spot of a chunk is: damp lowlands grow grass and dry ground stays sand.</summary>
    private GeneratedTexture Splat(int chunkX, int chunkZ)
    {
        var pixels = new byte[SplatSize * SplatSize * 4];
        for (int row = 0; row < SplatSize; row++)
        {
            for (int column = 0; column < SplatSize; column++)
            {
                // The edge texels sit on the chunk's edges, so neighbours agree there.
                float x = (chunkX + column / (SplatSize - 1f)) * ChunkSize;
                float z = (chunkZ + row / (SplatSize - 1f)) * ChunkSize;
                float damp = moisture.Layered(x * 0.01f, z * 0.01f, 3);
                float low = 1f - Math.Clamp((hills.Height(x, z) - 4f) / 14f, 0f, 1f);
                float grassy = Math.Clamp((damp * 0.7f + low * 0.5f - 0.35f) * 3f, 0f, 1f);
                int offset = (row * SplatSize + column) * 4;
                pixels[offset] = (byte)(grassy * 255f);
                pixels[offset + 1] = 0;
                pixels[offset + 2] = 0;
                pixels[offset + 3] = 255;
            }
        }

        return new GeneratedTexture(SplatSize, pixels);
    }

    private readonly record struct Chunk(ModelRef Model, TextureRef Splat, Entity Entity);
}
