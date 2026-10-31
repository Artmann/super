using System.Numerics;
using Rendition;

namespace Super.Tests;

public sealed class MeshDetailTests
{
    private static readonly Matrix4x4 Camera =
        Matrix4x4.CreateLookAt(new Vector3(0f, 0f, 10f), Vector3.Zero, Vector3.UnitY)
        * Projection.PerspectiveInfiniteReversedZ(MathF.PI / 2f, 1f, 0.1f);

    /// <summary>A flat grid facing +z, one unit wide.</summary>
    private static (Vertex[] Vertices, uint[] Indices) Grid(int quads)
    {
        var vertices = new Vertex[(quads + 1) * (quads + 1)];
        for (int i = 0; i < vertices.Length; i++)
        {
            var position = new Vector3(i % (quads + 1), i / (quads + 1), 0f) / quads;
            vertices[i] = new Vertex(position, Vector3.UnitZ, Vector4.UnitX, Vector2.Zero);
        }

        var indices = new List<uint>();
        for (int y = 0; y < quads; y++)
        {
            for (int x = 0; x < quads; x++)
            {
                uint corner = (uint)(y * (quads + 1) + x);
                uint above = corner + (uint)quads + 1;
                indices.AddRange([corner, corner + 1, above, above, corner + 1, above + 1]);
            }
        }

        return (vertices, indices.ToArray());
    }

    private static List<PartDetail> Details(float radius, params DetailLevel[] levels) =>
        [default, new PartDetail(Vector3.Zero, radius, levels)];

    private static DrawItem At(Vector3 position) => new()
    {
        IndexCount = 300,
        Transform = Matrix4x4.CreateTranslation(position),
        Detail = new DetailHandle(1),
    };

    private static DetailView View(float maxError = 1f) => new(Camera, new Vector3(0f, 0f, 10f), 500f, 0f, maxError);

    [Fact]
    public void Simplifying_uses_fewer_triangles_of_the_same_vertices()
    {
        var (vertices, indices) = Grid(32);

        uint[] simpler = MeshDetail.Simplify(vertices, indices, 0.25f);

        Assert.InRange(simpler.Length, 3, indices.Length / 4);
        Assert.All(simpler, index => Assert.Contains(index, indices));
    }

    [Fact]
    public void Simplifying_keeps_triangles_facing_the_same_way()
    {
        var (vertices, indices) = Grid(32);

        uint[] simpler = MeshDetail.Simplify(vertices, indices, 0.25f);

        for (int i = 0; i < simpler.Length; i += 3)
        {
            var a = vertices[simpler[i]].Position;
            var b = vertices[simpler[i + 1]].Position;
            var c = vertices[simpler[i + 2]].Position;
            Assert.True(Vector3.Cross(b - a, c - a).Z > 0f);
        }
    }

    [Fact]
    public void Levels_get_simpler_and_land_after_the_original_indices()
    {
        var (vertices, indices) = Grid(64);
        var all = new List<uint>(indices);

        var detail = MeshDetail.Build(vertices, indices, all);

        Assert.True(detail.Levels.Length > 1);
        Assert.Equal((uint)indices.Length, detail.Levels[0].FirstIndex);
        for (int i = 1; i < detail.Levels.Length; i++)
        {
            Assert.True(detail.Levels[i].IndexCount < detail.Levels[i - 1].IndexCount);
            Assert.True(detail.Levels[i].Error > detail.Levels[i - 1].Error);
        }

        Assert.Equal(new Vector3(0.5f, 0.5f, 0f), detail.Center);
    }

    [Fact]
    public void Draws_out_of_view_are_left_out_and_the_order_stays()
    {
        DrawItem[] draws = [At(new Vector3(-1f, 0f, 0f)), At(new Vector3(500f, 0f, 0f)), At(new Vector3(0f, 0f, 50f)), At(new Vector3(1f, 0f, 0f))];
        var selected = new DrawItem[draws.Length];

        int count = DrawSelection.Select(View(), draws, Details(1f), selected);

        Assert.Equal(2, count);
        Assert.Equal(-1f, selected[0].Transform.M41);
        Assert.Equal(1f, selected[1].Transform.M41);
    }

    [Fact]
    public void A_draw_just_past_the_edge_stays_while_its_sphere_shows()
    {
        // At this distance the view is 20 wide, so x = 10.5 is outside it by half a unit.
        DrawItem[] draws = [At(new Vector3(10.5f, 0f, 0f))];
        var selected = new DrawItem[1];

        Assert.Equal(1, DrawSelection.Select(View(), draws, Details(1f), selected));
        Assert.Equal(0, DrawSelection.Select(View(), draws, Details(0.1f), selected));
    }

    [Fact]
    public void A_swimming_tail_counts_towards_the_sphere()
    {
        DrawItem[] draws = [At(new Vector3(10.5f, 0f, 0f)) with { Swim = new SwimMotion(1f, 1f, 0f) }];

        Assert.Equal(1, DrawSelection.Select(View(), draws, Details(0.1f), new DrawItem[1]));
    }

    [Fact]
    public void Draws_without_detail_always_stay_in_full()
    {
        DrawItem[] draws = [At(new Vector3(500f, 0f, 0f)) with { Detail = default }];
        var selected = new DrawItem[1];

        Assert.Equal(1, DrawSelection.Select(View(), draws, Details(1f), selected));
        Assert.Equal(300u, selected[0].IndexCount);
    }

    [Fact]
    public void Far_draws_get_the_simplest_level_that_no_one_would_notice()
    {
        var details = Details(0.1f, new DetailLevel(300, 100, 0.01f), new DetailLevel(400, 30, 0.1f));
        var selected = new DrawItem[3];

        // 500 pixels at one unit away: 50 per unit from 10 away, 5 from 100, 1,000 from 0.5.
        DrawItem[] draws = [At(new Vector3(0f, 0f, -90f)), At(Vector3.Zero), At(new Vector3(0f, 0f, 9.5f))];
        DrawSelection.Select(View(), draws, details, selected);

        Assert.Equal(30u, selected[0].IndexCount);
        Assert.Equal(100u, selected[1].IndexCount);
        Assert.Equal(300u, selected[2].IndexCount);
    }

    [Fact]
    public void A_scaled_model_keeps_its_detail_for_longer()
    {
        var details = Details(0.1f, new DetailLevel(300, 100, 0.01f));
        DrawItem[] draws = [At(Vector3.Zero) with { Transform = Matrix4x4.CreateScale(10f) }];
        var selected = new DrawItem[1];

        DrawSelection.Select(View(), draws, details, selected);

        Assert.Equal(300u, selected[0].IndexCount);
    }
}
