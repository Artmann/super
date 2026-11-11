using System.Numerics;
using Rendition;

namespace Super.Tests;

public class LightmapLayoutTests
{
    private const ulong InstanceA = 0xA000_0000_0000_1000ul;
    private const ulong InstanceB = 0xB000_0000_0000_2000ul;

    private static DrawItem Draw(ulong bakeId, int mesh = 0, int material = 0, Matrix4x4? transform = null) => new()
    {
        BakeId = bakeId,
        Mesh = new MeshHandle(mesh),
        Material = new MaterialHandle(material),
        Transform = transform ?? Matrix4x4.Identity,
    };

    // Every layout here is built with default settings, which are 20 texels a metre, so a side is 20 times the square
    // root of the area in square metres.
    private static LightmapLayout Build(DrawItem[] draws, float[] areas, Func<MaterialHandle, bool>? getsLightmap = null) =>
        LightmapLayout.Build(draws, (mesh, _, _) => areas[mesh.Index], default, getsLightmap ?? (_ => true));

    // A mesh of a wall of two triangles with 5 square metres each (indices 0 to 5) and a trim of one triangle with
    // legs of 1 metre (indices 6 to 8).
    private static float[] WallAndTrim()
    {
        Vertex[] vertices =
        [
            new(new Vector3(0, 0, 0), Vector3.UnitY, Vector4.Zero, Vector2.Zero),
            new(new Vector3(4, 0, 0), Vector3.UnitY, Vector4.Zero, Vector2.Zero),
            new(new Vector3(4, 2.5f, 0), Vector3.UnitY, Vector4.Zero, Vector2.Zero),
            new(new Vector3(0, 2.5f, 0), Vector3.UnitY, Vector4.Zero, Vector2.Zero),
            new(new Vector3(10, 0, 0), Vector3.UnitY, Vector4.Zero, Vector2.Zero),
            new(new Vector3(11, 0, 0), Vector3.UnitY, Vector4.Zero, Vector2.Zero),
            new(new Vector3(10, 1, 0), Vector3.UnitY, Vector4.Zero, Vector2.Zero),
        ];
        uint[] indices = [0, 1, 2, 0, 2, 3, 4, 5, 6];
        return LightmapLayout.CumulativeAreas(vertices, indices);
    }

    [Fact]
    public void Each_draw_is_sized_by_the_triangles_in_its_index_range()
    {
        var cumulative = WallAndTrim();
        var wall = Draw(BakeIds.Of(InstanceA, 0), material: 1);
        wall.FirstIndex = 0;
        wall.IndexCount = 6;
        var trim = Draw(BakeIds.Of(InstanceA, 1), material: 2);
        trim.FirstIndex = 6;
        trim.IndexCount = 3;

        var layout = LightmapLayout.Build(
            [wall, trim], (_, first, count) => LightmapLayout.RangeArea(cumulative, first, count), default, _ => true);

        layout.TryGet(wall.BakeId, out var wallRect);
        layout.TryGet(trim.BakeId, out var trimRect);
        // sqrt(10) * 20 is 63.2 and sqrt(0.5) * 20 is 14.1. The whole mesh would give both 65.
        Assert.Equal(63, SideInTexels(wallRect));
        Assert.Equal(14, SideInTexels(trimRect));
    }

    [Fact]
    public void Draws_of_one_mesh_that_share_a_material_add_their_ranges()
    {
        var cumulative = WallAndTrim();
        var first = Draw(BakeIds.Of(InstanceA, 0), material: 1);
        first.FirstIndex = 0;
        first.IndexCount = 3;
        var second = Draw(BakeIds.Of(InstanceA, 1), material: 1);
        second.FirstIndex = 3;
        second.IndexCount = 3;

        var layout = LightmapLayout.Build(
            [first, second], (_, from, count) => LightmapLayout.RangeArea(cumulative, from, count), default, _ => true);

        layout.TryGet(first.BakeId, out var firstRect);
        layout.TryGet(second.BakeId, out var secondRect);
        // 5 + 5 square metres, so sqrt(10) * 20 is 63, where either alone would be sqrt(5) * 20 = 45.
        Assert.Equal(63, SideInTexels(firstRect));
        Assert.Equal(firstRect, secondRect);
    }

    [Fact]
    public void An_index_range_reads_only_the_triangles_it_covers_and_stops_at_the_mesh_end()
    {
        var cumulative = WallAndTrim();

        Assert.Equal(5f, LightmapLayout.RangeArea(cumulative, 3, 3), 4);
        Assert.Equal(0.5f, LightmapLayout.RangeArea(cumulative, 6, 3), 4);
        Assert.Equal(0.5f, LightmapLayout.RangeArea(cumulative, 6, 300), 4);
        Assert.Equal(0f, LightmapLayout.RangeArea(cumulative, 300, 3));
    }

    [Fact]
    public void Two_meshes_with_one_instance_transform_and_material_are_refused_naming_the_instance()
    {
        var draws = new[]
        {
            Draw(BakeIds.Of(InstanceA, 0), mesh: 0, material: 1),
            Draw(BakeIds.Of(InstanceA, 1), mesh: 1, material: 1),
        };

        var error = Assert.Throws<InvalidOperationException>(() => Build(draws, [1f, 1f]));

        Assert.Contains("0xA000000000001000", error.Message);
        Assert.Contains("two different meshes", error.Message);
    }

    private static int SideInTexels(LightmapRect rect) => (int)MathF.Round(rect.Scale.X * LightmapLayout.PageSize);

    [Fact]
    public void A_one_metre_quad_at_twenty_texels_gets_a_twenty_texel_side()
    {
        var layout = Build([Draw(BakeIds.Of(InstanceA, 0))], [1f]);

        Assert.True(layout.TryGet(BakeIds.Of(InstanceA, 0), out var rect));
        Assert.Equal(20, SideInTexels(rect));
        Assert.Equal(20, (int)MathF.Round(rect.Scale.Y * LightmapLayout.PageSize));
        Assert.Equal(1, layout.Pages);
    }

    [Fact]
    public void Sides_clamp_to_eight_and_1024()
    {
        var tiny = Build([Draw(BakeIds.Of(InstanceA, 0))], [0.0001f]);
        var huge = Build([Draw(BakeIds.Of(InstanceA, 0))], [1_000_000f]);

        tiny.TryGet(BakeIds.Of(InstanceA, 0), out var small);
        huge.TryGet(BakeIds.Of(InstanceA, 0), out var big);
        Assert.Equal(LightmapLayout.MinSide, SideInTexels(small));
        Assert.Equal(LightmapLayout.MaxSide, SideInTexels(big));
    }

    [Fact]
    public void Parts_of_one_instance_with_one_material_share_a_rectangle()
    {
        var draws = new[]
        {
            Draw(BakeIds.Of(InstanceA, 0), mesh: 0, material: 1),
            Draw(BakeIds.Of(InstanceA, 1), mesh: 0, material: 1),
            Draw(BakeIds.Of(InstanceA, 2), mesh: 0, material: 2),
            Draw(BakeIds.Of(InstanceB, 0), mesh: 0, material: 1),
        };

        var layout = Build(draws, [1f]);

        layout.TryGet(BakeIds.Of(InstanceA, 0), out var first);
        layout.TryGet(BakeIds.Of(InstanceA, 1), out var second);
        layout.TryGet(BakeIds.Of(InstanceA, 2), out var otherMaterial);
        layout.TryGet(BakeIds.Of(InstanceB, 0), out var otherInstance);
        Assert.Equal(first, second);
        Assert.NotEqual(first, otherMaterial);
        Assert.NotEqual(first, otherInstance);
        Assert.Equal(4, layout.Draws.Count);
    }

    [Fact]
    public void Draws_of_one_instance_in_different_places_get_their_own_rectangles()
    {
        var draws = new[]
        {
            Draw(BakeIds.Of(InstanceA, 0)),
            Draw(BakeIds.Of(InstanceA, 1), transform: Matrix4x4.CreateTranslation(5f, 0f, 0f)),
        };

        var layout = Build(draws, [1f]);

        layout.TryGet(BakeIds.Of(InstanceA, 0), out var first);
        layout.TryGet(BakeIds.Of(InstanceA, 1), out var second);
        Assert.NotEqual(first.Offset, second.Offset);
    }

    [Fact]
    public void A_scaled_transform_scales_the_area()
    {
        var layout = Build([Draw(BakeIds.Of(InstanceA, 0), transform: Matrix4x4.CreateScale(2f))], [1f]);

        layout.TryGet(BakeIds.Of(InstanceA, 0), out var rect);
        Assert.Equal(40, SideInTexels(rect));
    }

    [Fact]
    public void Draws_that_are_not_static_get_no_rectangle()
    {
        var layout = Build([Draw(0), Draw(BakeIds.Of(InstanceA, 0))], [1f]);

        Assert.Single(layout.Draws);
        Assert.False(layout.TryGet(0, out _));
    }

    [Fact]
    public void Wind_and_transparent_draws_get_no_rectangle()
    {
        var draws = new[]
        {
            Draw(BakeIds.Of(InstanceA, 0), material: 0),
            Draw(BakeIds.Of(InstanceA, 1), material: 1),
            Draw(BakeIds.Of(InstanceA, 2), material: 2),
        };

        // Material 1 sways in the wind and material 2 is glass.
        var layout = Build(draws, [1f], material => material.Index == 0);

        Assert.True(layout.TryGet(BakeIds.Of(InstanceA, 0), out _));
        Assert.False(layout.TryGet(BakeIds.Of(InstanceA, 1), out _));
        Assert.False(layout.TryGet(BakeIds.Of(InstanceA, 2), out _));
        Assert.Single(layout.Draws);
        Assert.Empty(layout.Left);
    }

    [Fact]
    public void A_draw_that_does_not_fit_eight_pages_is_left_out_and_named()
    {
        // A 1024 side plus its gutter leaves room for only one on a 2048 page.
        float area = 1024f * 1024f / (20f * 20f);
        var draws = Enumerable.Range(0, 9).Select(i => Draw(BakeIds.Of(InstanceA, i), material: i)).ToArray();

        var layout = Build(draws, [area]);

        Assert.Equal(LightmapLayout.MaxPages, layout.Pages);
        Assert.Equal(8, layout.Draws.Count);
        Assert.Equal([BakeIds.Of(InstanceA, 8)], layout.Left);
        Assert.False(layout.TryGet(BakeIds.Of(InstanceA, 8), out _));
        Assert.Equal(Enumerable.Range(0, 8).Select(i => i), layout.Draws.Select(d => d.Rect.Page).Order());
    }

    [Fact]
    public void Everything_fits_when_there_is_room_so_nothing_is_left()
    {
        var draws = Enumerable.Range(0, 100).Select(i => Draw(BakeIds.Of(InstanceA, i), material: i)).ToArray();

        var layout = Build(draws, [4f]);

        Assert.Empty(layout.Left);
        Assert.Equal(100, layout.Draws.Count);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Rectangles_never_overlap_and_keep_a_gutter(int seed)
    {
        var random = new Random(seed);
        var areas = new float[400];
        for (int i = 0; i < areas.Length; i++)
        {
            // From a few texels to the 1024 cap.
            areas[i] = MathF.Pow(random.NextSingle() * 50f, 2f) + 0.01f;
        }

        var draws = Enumerable.Range(0, areas.Length).Select(i => Draw(BakeIds.Of(InstanceA, i), mesh: i, material: i)).ToArray();
        var layout = Build(draws, areas);

        Assert.True(layout.Pages > 1, "the test needs more than one page to mean anything");
        var placed = layout.Draws.Select(d => d.Rect).ToArray();
        Assert.Equal(areas.Length - layout.Left.Count, placed.Length);
        int gutter = LightmapUnwrap.Gutter;
        for (int a = 0; a < placed.Length; a++)
        {
            var ra = ToTexels(placed[a]);
            Assert.True(ra.X >= 0 && ra.Y >= 0 && ra.X + ra.Side <= LightmapLayout.PageSize && ra.Y + ra.Side <= LightmapLayout.PageSize,
                $"rectangle {a} leaves the page");
            for (int b = a + 1; b < placed.Length; b++)
            {
                if (placed[a].Page != placed[b].Page)
                {
                    continue;
                }

                var rb = ToTexels(placed[b]);
                bool apartX = ra.X + ra.Side + gutter <= rb.X || rb.X + rb.Side + gutter <= ra.X;
                bool apartY = ra.Y + ra.Side + gutter <= rb.Y || rb.Y + rb.Side + gutter <= ra.Y;
                Assert.True(apartX || apartY, $"rectangles {a} and {b} on page {placed[a].Page} are closer than the gutter");
            }
        }
    }

    [Fact]
    public void A_meshs_surface_area_is_the_sum_of_its_triangles_ignoring_where_it_stands()
    {
        // A 2 by 3 rectangle in two triangles, and a right triangle with legs 4 and 1 sharing one corner.
        Vertex[] vertices =
        [
            new(new Vector3(0, 0, 0), Vector3.UnitY, Vector4.Zero, Vector2.Zero),
            new(new Vector3(2, 0, 0), Vector3.UnitY, Vector4.Zero, Vector2.Zero),
            new(new Vector3(2, 0, 3), Vector3.UnitY, Vector4.Zero, Vector2.Zero),
            new(new Vector3(0, 0, 3), Vector3.UnitY, Vector4.Zero, Vector2.Zero),
            new(new Vector3(0, 4, 0), Vector3.UnitY, Vector4.Zero, Vector2.Zero),
            new(new Vector3(0, 0, 1), Vector3.UnitY, Vector4.Zero, Vector2.Zero),
        ];
        uint[] indices = [0, 1, 2, 0, 2, 3, 0, 4, 5];

        Assert.Equal(8f, LightmapLayout.CumulativeAreas(vertices, indices)[^1], 4);
    }

    public static TheoryData<int> Seeds() => [1, 2, 3, 4, 5, 6, 7, 8, 9, 10];

    private static (int X, int Y, int Side) ToTexels(LightmapRect rect) => (
        (int)MathF.Round(rect.Offset.X * LightmapLayout.PageSize),
        (int)MathF.Round(rect.Offset.Y * LightmapLayout.PageSize),
        SideInTexels(rect));
}
