using System.Numerics;
using Rendition;
using Super.Terrain;

namespace Super.Tests;

public class HeightfieldTests
{
    private const int Quads = 8;
    private const float Spacing = 1.5f;

    private static float Wavy(float x, float z) => MathF.Sin(x * 0.37f) * MathF.Cos(z * 0.21f);

    private static Vertex[] Column(GeneratedMesh chunk, int column)
    {
        int side = Quads + 1;
        var vertices = new Vertex[side];
        for (int row = 0; row < side; row++)
        {
            vertices[row] = chunk.Vertices[row * side + column];
        }

        return vertices;
    }

    [Fact]
    public void Neighbouring_chunks_share_border_vertices_exactly()
    {
        var field = new Heightfield(Wavy, Spacing);
        var surface = new Surface();

        var left = field.Chunk(0, 0, Quads, surface, 0.25f);
        var right = field.Chunk(1, 0, Quads, surface, 0.25f);

        var leftEdge = Column(left, Quads);
        var rightEdge = Column(right, 0);
        for (int row = 0; row <= Quads; row++)
        {
            Assert.Equal(leftEdge[row].Position, rightEdge[row].Position);
            Assert.Equal(leftEdge[row].Normal, rightEdge[row].Normal);
            Assert.Equal(leftEdge[row].Uv, rightEdge[row].Uv);
        }
    }

    [Fact]
    public void Normal_of_a_slope_tilts_against_it()
    {
        var field = new Heightfield((x, _) => x, 1f);

        var normal = field.Normal(3f, -2f);
        var expected = Vector3.Normalize(new Vector3(-1f, 1f, 0f));

        Assert.InRange(Vector3.Distance(normal, expected), 0f, 1e-5f);
        Assert.Equal(3f, field.Height(3f, -2f));
    }

    [Fact]
    public void Chunk_is_not_simplified()
    {
        var field = new Heightfield(Wavy, Spacing);

        var chunk = field.Chunk(2, -1, Quads, new Surface(), 1f);

        Assert.False(chunk.Simplify);
    }

    [Fact]
    public void Chunk_covers_quads_times_spacing_from_its_corner()
    {
        var field = new Heightfield(Wavy, Spacing);
        var surface = new Surface { Roughness = 0.8f };

        var chunk = field.Chunk(2, -1, Quads, surface, 0.5f);

        float size = Quads * Spacing;
        Assert.Equal((Quads + 1) * (Quads + 1), chunk.Vertices.Length);
        Assert.Equal(Quads * Quads * 6, chunk.Indices.Length);
        Assert.Equal(new Vector2(2f * size, -size), new Vector2(chunk.Vertices[0].Position.X, chunk.Vertices[0].Position.Z));
        Assert.Equal(new Vector2(3f * size, 0f), new Vector2(chunk.Vertices[^1].Position.X, chunk.Vertices[^1].Position.Z));
        Assert.Equal(new Vector2(2f * size, -size) * 0.5f, chunk.Vertices[0].Uv);
        Assert.Same(surface, chunk.Surface);
    }

    [Fact]
    public void Triangles_face_up()
    {
        var field = new Heightfield((_, _) => 0f, 1f);

        var chunk = field.Chunk(0, 0, 1, new Surface(), 1f);

        for (int i = 0; i < chunk.Indices.Length; i += 3)
        {
            var a = chunk.Vertices[chunk.Indices[i]].Position;
            var b = chunk.Vertices[chunk.Indices[i + 1]].Position;
            var c = chunk.Vertices[chunk.Indices[i + 2]].Position;
            Assert.True(Vector3.Cross(b - a, c - a).Y > 0f);
        }
    }
}
