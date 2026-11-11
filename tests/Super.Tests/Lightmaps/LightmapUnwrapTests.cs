using System.Globalization;
using System.Numerics;
using Rendition;

namespace Super.Tests;

public class LightmapUnwrapTests
{
    public static TheoryData<string> Meshes =>
    [
        "welded sphere", "seam-split sphere", "box", "helical ramp", "single triangle", "unshared triangles",
        "mirrored box", "zero-area triangles",
    ];

    [Theory]
    [MemberData(nameof(Meshes))]
    public void Makes_a_valid_lightmap_set(string name)
    {
        var (vertices, indices) = TestShapes.Get(name);
        var (outVertices, outIndices) = LightmapUnwrap.Unwrap(vertices, indices);
        Assert.Null(LightmapUnwrap.Problem(
            outVertices.Select(v => v.Position).ToArray(), outVertices.Select(v => v.Uv2).ToArray(), outIndices));
    }

    [Fact]
    public void Keeps_triangle_order_and_every_corner()
    {
        var (vertices, indices) = TestShapes.Get("seam-split sphere");
        var (outVertices, outIndices) = LightmapUnwrap.Unwrap(vertices, indices);
        Assert.Equal(indices.Length, outIndices.Length);
        for (int i = 0; i < indices.Length; i++)
        {
            var before = vertices[(int)indices[i]];
            var after = outVertices[(int)outIndices[i]];
            Assert.Equal(before with { Uv2 = after.Uv2 }, after);
        }
    }

    [Fact]
    public void Splits_vertices_only_where_charts_meet()
    {
        var (vertices, indices) = TestShapes.Get("box");
        var (outVertices, _) = LightmapUnwrap.Unwrap(vertices, indices);

        // The box's 24 vertices already split at every edge, and each face is a chart, so none more are needed.
        Assert.Equal(24, outVertices.Length);
    }

    [Fact]
    public void Density_is_even_across_charts()
    {
        var (vertices, indices) = TestShapes.Get("box with one long side");
        var (v, i) = LightmapUnwrap.Unwrap(vertices, indices);
        Assert.InRange(LightmapUnwrap.DensitySpread(v.Select(x => x.Position).ToArray(), v.Select(x => x.Uv2).ToArray(), i), 1.0, 1.01);
    }

    [Fact]
    public void Density_is_even_on_a_curved_mesh()
    {
        // Sphere charts are tilted from their projection plane by different amounts, which each chart's scale undoes.
        var (vertices, indices) = TestShapes.Get("welded sphere");
        var (v, i) = LightmapUnwrap.Unwrap(vertices, indices);
        Assert.InRange(LightmapUnwrap.DensitySpread(v.Select(x => x.Position).ToArray(), v.Select(x => x.Uv2).ToArray(), i), 1.0, 1.01);
    }

    [Fact]
    public void A_box_is_six_charts() =>
        Assert.Equal(6, LightmapUnwrap.Charts(TestShapes.Get("box").Vertices, TestShapes.Get("box").Indices));

    [Fact]
    public void A_mirrored_box_is_six_charts() =>
        Assert.Equal(6, LightmapUnwrap.Charts(TestShapes.Get("mirrored box").Vertices, TestShapes.Get("mirrored box").Indices));

    [Fact]
    public void A_seam_in_the_first_uvs_does_not_split_a_chart()
    {
        int welded = LightmapUnwrap.Charts(TestShapes.Get("welded sphere").Vertices, TestShapes.Get("welded sphere").Indices);
        int split = LightmapUnwrap.Charts(TestShapes.Get("seam-split sphere").Vertices, TestShapes.Get("seam-split sphere").Indices);
        Assert.Equal(welded, split);
    }

    [Fact]
    public void A_ramp_that_turns_over_itself_is_split()
    {
        // Every normal is within 10 degrees of up, so only the overlap check splits it.
        var (vertices, indices) = TestShapes.Get("helical ramp");
        Assert.True(LightmapUnwrap.Charts(vertices, indices) >= 2);
    }

    [Fact]
    public void A_long_thin_strip_still_fits()
    {
        var (vertices, indices) = TestShapes.Get("strip 40 by 0.2");
        var (v, i) = LightmapUnwrap.Unwrap(vertices, indices);
        Assert.Null(LightmapUnwrap.Problem(v.Select(x => x.Position).ToArray(), v.Select(x => x.Uv2).ToArray(), i));
    }

    [Fact]
    public void Fills_the_page_well()
    {
        // One square: nothing but the margin and rounding keeps it from filling the page.
        Vector3[] positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f)];
        var vertices = positions.Select(p => new Vertex(p, Vector3.UnitZ, new Vector4(1f, 0f, 0f, 1f), Vector2.Zero)).ToArray();
        var (v, i) = LightmapUnwrap.Unwrap(vertices, [0u, 1u, 2u, 0u, 2u, 3u]);
        var uvs = v.Select(x => x.Uv2).ToArray();
        float width = uvs.Max(uv => uv.X) - uvs.Min(uv => uv.X);
        Assert.InRange(width, 0.9f, 1f);
    }
}

public class LightmapUnwrapProblemTests
{
    private static readonly Vector3[] TwoTriangles =
    [
        new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f),
        new(0f, 0f, 5f), new(1f, 0f, 5f), new(0f, 1f, 5f),
    ];

    [Fact]
    public void Names_two_overlapping_triangles()
    {
        // Two triangles in different places with the same UVs. Triangle 1's first texel, row by row, is the one
        // round its corner at (51.2, 51.2) texels.
        Vector2[] uvs = [new(0.1f, 0.1f), new(0.3f, 0.1f), new(0.1f, 0.3f), new(0.1f, 0.1f), new(0.3f, 0.1f), new(0.1f, 0.3f)];
        Assert.Equal(
            "Triangles 0 and 1 overlap at texel (51, 51).",
            LightmapUnwrap.Problem(TwoTriangles, uvs, [0u, 1u, 2u, 3u, 4u, 5u]));
    }

    [Fact]
    public void Names_a_chart_that_overlaps_itself()
    {
        // The same place twice with the same UVs: one chart, folded onto itself.
        Vector3[] positions = [new(0f, 0f, 0f), new(1f, 0f, 0f), new(0f, 1f, 0f), new(1f, 1f, 0f)];
        Vector2[] uvs = [new(0.1f, 0.1f), new(0.3f, 0.1f), new(0.1f, 0.3f), new(0.05f, 0.05f)];

        // Triangle 1 shares triangle 0's long edge but lies on the same side of it, all over it. The overlap is
        // triangle 0, whose middle is at (1/6, 1/6), texel (85.3, 85.3).
        Assert.Equal(
            "Triangles 0 and 1 overlap at texel (85, 85).",
            LightmapUnwrap.Problem(positions, uvs, [0u, 1u, 2u, 1u, 2u, 3u]));
    }

    [Fact]
    public void Names_a_corner_outside_0_to_1()
    {
        Vector2[] uvs = [new(0.1f, 0.1f), new(0.3f, 0.1f), new(0.1f, 0.3f), new(0.5f, 0.5f), new(1.25f, 0.5f), new(0.5f, 0.7f)];
        Assert.Equal(
            "Triangle 1 reaches outside 0 to 1, to (1.25, 0.5).",
            LightmapUnwrap.Problem(TwoTriangles, uvs, [0u, 1u, 2u, 3u, 4u, 5u]));
    }

    [Fact]
    public void Names_charts_closer_than_the_gutter()
    {
        // Two quads at 512 texels: one covers texels 10 to 19 across, the other 21 to 30, so one texel lies between.
        Vector3[] positions =
        [
            new(0f, 0f, 0f), new(1f, 0f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f),
            new(0f, 0f, 5f), new(1f, 0f, 5f), new(1f, 1f, 5f), new(0f, 1f, 5f),
        ];
        Vector2[] uvs =
        [
            Texels(10.5f, 10.5f), Texels(19.5f, 10.5f), Texels(19.5f, 19.5f), Texels(10.5f, 19.5f),
            Texels(21.5f, 10.5f), Texels(30.5f, 10.5f), Texels(30.5f, 19.5f), Texels(21.5f, 19.5f),
        ];
        Assert.Equal(
            "Triangles 0 and 2 are in different charts with only 1 empty texel between them at texel (19, 10), less than the 2-texel gutter.",
            LightmapUnwrap.Problem(positions, uvs, [0u, 1u, 2u, 0u, 2u, 3u, 4u, 5u, 6u, 4u, 6u, 7u]));
    }

    [Fact]
    public void Names_uneven_density()
    {
        // A 1 m² square and a 0.1 m² strip, given UV squares of the same size: the strip is 10 times as dense.
        Vector3[] positions =
        [
            new(0f, 0f, 0f), new(1f, 0f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f),
            new(0f, 0f, 5f), new(1f, 0f, 5f), new(1f, 0.1f, 5f), new(0f, 0.1f, 5f),
        ];
        Vector2[] uvs =
        [
            new(0.1f, 0.1f), new(0.3f, 0.1f), new(0.3f, 0.3f), new(0.1f, 0.3f),
            new(0.6f, 0.6f), new(0.8f, 0.6f), new(0.8f, 0.8f), new(0.6f, 0.8f),
        ];
        uint[] indices = [0u, 1u, 2u, 0u, 2u, 3u, 4u, 5u, 6u, 4u, 6u, 7u];
        Assert.Equal(
            "Texel density varies 10× across the mesh, from the chart of triangle 0 to the chart of triangle 2, and must vary less than 4×.",
            LightmapUnwrap.Problem(positions, uvs, indices));
        Assert.Equal(10.0, LightmapUnwrap.DensitySpread(positions, uvs, indices), 3);
    }

    [Fact]
    public void Writes_numbers_the_same_in_every_culture()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            Vector2[] uvs = [new(0.1f, 0.1f), new(0.3f, 0.1f), new(0.1f, 0.3f), new(0.5f, 0.5f), new(1.25f, 0.5f), new(0.5f, 0.7f)];
            Assert.Equal(
                "Triangle 1 reaches outside 0 to 1, to (1.25, 0.5).",
                LightmapUnwrap.Problem(TwoTriangles, uvs, [0u, 1u, 2u, 3u, 4u, 5u]));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Accepts_charts_two_texels_apart()
    {
        // As in the gutter test, but with texels 20 and 21 empty between the quads.
        Vector3[] positions =
        [
            new(0f, 0f, 0f), new(1f, 0f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f),
            new(0f, 0f, 5f), new(1f, 0f, 5f), new(1f, 1f, 5f), new(0f, 1f, 5f),
        ];
        Vector2[] uvs =
        [
            Texels(10.5f, 10.5f), Texels(19.5f, 10.5f), Texels(19.5f, 19.5f), Texels(10.5f, 19.5f),
            Texels(22.5f, 10.5f), Texels(31.5f, 10.5f), Texels(31.5f, 19.5f), Texels(22.5f, 19.5f),
        ];
        Assert.Null(LightmapUnwrap.Problem(positions, uvs, [0u, 1u, 2u, 0u, 2u, 3u, 4u, 5u, 6u, 4u, 6u, 7u]));
    }

    private static Vector2 Texels(float x, float y) => new Vector2(x, y) / LightmapUnwrap.ReferenceSize;
}
