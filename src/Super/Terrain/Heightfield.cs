using System.Numerics;
using Rendition;

namespace Super.Terrain;

/// <summary>
/// Ground as heights on a grid. Builds chunk meshes and answers how high the ground is
/// anywhere. Chunks sample the same function on the same grid, so neighbours meet
/// exactly, and normals come from the function rather than the mesh, so a chunk's edge
/// already knows about the ground beyond it.
/// </summary>
public sealed class Heightfield(Func<float, float, float> height, float spacing)
{
    /// <summary>How far apart the grid's vertices are, in metres.</summary>
    public float Spacing { get; } = spacing > 0f
        ? spacing
        : throw new ArgumentOutOfRangeException(nameof(spacing), spacing, "Vertices need some space between them.");

    /// <summary>How high the ground is at a point. The function itself; the mesh samples it on the grid.</summary>
    public float Height(float x, float z) => height(x, z);

    /// <summary>Which way the ground faces at a point, from the slopes one grid step either side.</summary>
    public Vector3 Normal(float x, float z)
    {
        float step = Spacing;
        float slopeX = height(x + step, z) - height(x - step, z);
        float slopeZ = height(x, z + step) - height(x, z - step);
        return Vector3.Normalize(new Vector3(-slopeX, 2f * step, -slopeZ));
    }

    /// <summary>
    /// The mesh for one chunk of <paramref name="quads"/> × <paramref name="quads"/> squares,
    /// starting at chunk coordinates times the chunk's size. UVs are world x and z times
    /// <paramref name="uvPerMetre"/>, so tiled textures continue across chunks. The second
    /// UVs, for the lightmap, are the vertex's place on the chunk's grid, 0 to 1 across it.
    /// It's never simplified, so its edges stay where the neighbours expect.
    /// </summary>
    public GeneratedMesh Chunk(int chunkX, int chunkZ, int quads, Surface surface, float uvPerMetre)
    {
        if (quads < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(quads), quads, "A chunk needs at least one quad.");
        }

        return new GeneratedMesh(ChunkVertices(chunkX, chunkZ, quads, uvPerMetre), ChunkIndices(quads), surface, Simplify: false);
    }

    /// <summary>The chunk's grid of vertices, row by row along z.</summary>
    private Vertex[] ChunkVertices(int chunkX, int chunkZ, int quads, float uvPerMetre)
    {
        int side = quads + 1;
        var vertices = new Vertex[side * side];
        for (int row = 0; row < side; row++)
        {
            for (int column = 0; column < side; column++)
            {
                // Every chunk computes a border vertex from the same grid indices, so the
                // bits match without any stitching.
                float x = (chunkX * quads + column) * Spacing;
                float z = (chunkZ * quads + row) * Spacing;
                vertices[row * side + column] = new Vertex(
                    new Vector3(x, height(x, z), z),
                    Normal(x, z),
                    new Vector4(1f, 0f, 0f, 1f),
                    new Vector2(x, z) * uvPerMetre)
                {
                    Uv2 = new Vector2(column, row) / quads,
                };
            }
        }

        return vertices;
    }

    /// <summary>Two triangles for each square of the grid.</summary>
    private static uint[] ChunkIndices(int quads)
    {
        int side = quads + 1;
        var indices = new uint[quads * quads * 6];
        int next = 0;
        for (int row = 0; row < quads; row++)
        {
            for (int column = 0; column < quads; column++)
            {
                uint corner = (uint)(row * side + column);
                uint below = corner + (uint)side;
                ReadOnlySpan<uint> square = [corner, below, corner + 1, corner + 1, below, below + 1];
                square.CopyTo(indices.AsSpan(next));
                next += square.Length;
            }
        }

        return indices;
    }
}
