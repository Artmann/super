using System.Globalization;
using System.Numerics;
using Rendition;

namespace Super.Tests;

public class LightmapUnwrapTests
{
    public static TheoryData<string> Meshes =>
    [
        "welded sphere", "seam-split sphere", "box", "helical ramp", "single triangle", "unshared triangles",
        "mirrored box", "zero-area triangles", "coarse helical ramp",
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
    public void A_ramp_is_cut_again_where_its_laid_out_turns_share_a_texel()
    {
        // Growing stops the first chart one full turn round, where it would overlap its start. Laid out, its last step
        // touches the texels of its first, a storey lower, so Unwrap has to cut it into more charts than growing made.
        var (vertices, indices) = TestShapes.Get("coarse helical ramp");
        int grown = LightmapUnwrap.Charts(vertices, indices);
        var (v, i) = LightmapUnwrap.Unwrap(vertices, indices);
        Assert.Null(LightmapUnwrap.Problem(v.Select(x => x.Position).ToArray(), v.Select(x => x.Uv2).ToArray(), i));
        Assert.True(OutputCharts(i) > grown, $"Unwrap made {OutputCharts(i)} charts, no more than the {grown} grown.");
    }

    public static TheoryData<string> CurvedMeshes => ["welded sphere", "bent strip"];

    [Theory]
    [MemberData(nameof(CurvedMeshes))]
    public void Density_is_within_four_times_from_triangle_to_triangle(string name)
    {
        var (vertices, indices) = TestShapes.Get(name);
        var (v, i) = LightmapUnwrap.Unwrap(vertices, indices);
        var densities = new List<double>();
        for (int t = 0; t < i.Length; t += 3)
        {
            var (a, b, c) = (v[i[t]], v[i[t + 1]], v[i[t + 2]]);
            double world = Vector3.Cross(b.Position - a.Position, c.Position - a.Position).Length();
            double uv = Math.Abs((b.Uv2.X - a.Uv2.X) * (c.Uv2.Y - a.Uv2.Y) - (c.Uv2.X - a.Uv2.X) * (b.Uv2.Y - a.Uv2.Y));
            densities.Add(uv / world);
        }

        Assert.InRange(densities.Max() / densities.Min(), 1.0, 4.0);
    }

    [Theory]
    [InlineData(10.0)]
    [InlineData(10.0000001)]
    [InlineData(9.9999999)]
    [InlineData(0.25)]
    public void A_chart_rectangle_holds_every_texel_the_chart_touches(double extent)
    {
        var (size, offset) = LightmapUnwrap.ChartRectangle(extent);

        // Texel k is touched when the closed span [k, k + 1] meets the closed span the chart covers.
        int first = (int)Math.Ceiling(offset) - 1;
        int last = (int)Math.Floor(offset + extent);
        Assert.InRange(first, 0, size - 1);
        Assert.InRange(last, 0, size - 1);
        Assert.Equal(offset, size - offset - extent, 9);
    }

    /// <summary>
    /// How many charts an unwrapped mesh has: Unwrap splits every vertex where charts meet, so triangles joined
    /// through shared vertices are one chart.
    /// </summary>
    private static int OutputCharts(uint[] indices)
    {
        int vertexCount = (int)indices.Max() + 1;
        var parent = Enumerable.Range(0, vertexCount).ToArray();
        int Find(int x)
        {
            while (parent[x] != x)
            {
                x = parent[x] = parent[parent[x]];
            }

            return x;
        }

        for (int corner = 0; corner < indices.Length; corner += 3)
        {
            parent[Find((int)indices[corner + 1])] = Find((int)indices[corner]);
            parent[Find((int)indices[corner + 2])] = Find((int)indices[corner]);
        }

        return indices.Select(index => Find((int)index)).Distinct().Count();
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

    /// <summary>
    /// <paramref name="count"/> triangles that share no corner, so each is a chart of its own, half a metre each in a
    /// grid of rows a metre apart.
    /// </summary>
    private static (Vertex[] Vertices, uint[] Indices) Scattered(int count)
    {
        var vertices = new Vertex[count * 3];
        int row = (int)Math.Ceiling(Math.Sqrt(count));
        for (int t = 0; t < count; t++)
        {
            var at = new Vector3(t % row, t / row, 0f);
            vertices[t * 3] = new Vertex(at, Vector3.UnitZ, new Vector4(1f, 0f, 0f, 1f), Vector2.Zero);
            vertices[t * 3 + 1] = new Vertex(at + new Vector3(0.5f, 0f, 0f), Vector3.UnitZ, new Vector4(1f, 0f, 0f, 1f), Vector2.Zero);
            vertices[t * 3 + 2] = new Vertex(at + new Vector3(0f, 0.5f, 0f), Vector3.UnitZ, new Vector4(1f, 0f, 0f, 1f), Vector2.Zero);
        }

        return (vertices, Enumerable.Range(0, count * 3).Select(index => (uint)index).ToArray());
    }

    // The smallest chart takes 1 texel and its 2-texel gutter, 3 texels each way, so 512 texels square holds at most
    // (512 / 3)² ≈ 29,000 charts and 1024 holds about 116,000.
    [Fact]
    public void Charts_that_dont_fit_512_texels_are_laid_out_at_1024()
    {
        var (vertices, indices) = Scattered(30_000);

        var error = Assert.Throws<InvalidOperationException>(() => LightmapUnwrap.Unwrap(vertices, indices));
        Assert.Equal("The mesh's 30000 lightmap charts don't fit a 512-texel page with a 2-texel gutter, even at their smallest.", error.Message);

        var (v, i) = LightmapUnwrap.Unwrap(vertices, indices, 1024);
        var positions = v.Select(x => x.Position).ToArray();
        var uvs = v.Select(x => x.Uv2).ToArray();
        Assert.Null(LightmapUnwrap.Problem(positions, uvs, i, 1024));
        Assert.NotNull(LightmapUnwrap.Problem(positions, uvs, i, 512));
    }

    [Fact]
    public void Charts_that_dont_fit_1024_texels_are_laid_out_at_2048()
    {
        var (vertices, indices) = Scattered(120_000);

        var error = Assert.Throws<InvalidOperationException>(() => LightmapUnwrap.Unwrap(vertices, indices, 1024));
        Assert.Equal("The mesh's 120000 lightmap charts don't fit a 1024-texel page with a 2-texel gutter, even at their smallest.", error.Message);

        var (v, i) = LightmapUnwrap.Unwrap(vertices, indices, 2048);
        var positions = v.Select(x => x.Position).ToArray();
        var uvs = v.Select(x => x.Uv2).ToArray();
        Assert.Null(LightmapUnwrap.Problem(positions, uvs, i, 2048));
        Assert.NotNull(LightmapUnwrap.Problem(positions, uvs, i, 1024));
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
    public void Names_a_chart_whose_arms_share_a_texel()
    {
        // Four 10-texel quads round the point (20, 20), one chart joined edge to edge, like a ramp turning once round a
        // post: 1 cm a texel across, rising to 1 m at the last quad's outer corner. The last quad's bottom edge lies on
        // the first quad's top edge in UV, but its corner at texel (10, 20) is 1 m above the first quad's. No area
        // overlaps, yet the texels along that edge would get both storeys' light.
        Vector2[] uv =
        [
            Texels(10f, 10f), Texels(20f, 10f), Texels(20f, 20f), Texels(10f, 20f),
            Texels(30f, 10f), Texels(30f, 20f), Texels(30f, 30f), Texels(20f, 30f), Texels(10f, 30f), Texels(10f, 20f),
        ];
        float[] heights = [0f, 0f, 0f, 0f, 0.25f, 0.5f, 0.6f, 0.75f, 1f, 1f];
        var positions = uv.Select((p, k) => new Vector3(p.X * LightmapUnwrap.ReferenceSize / 100f, p.Y * LightmapUnwrap.ReferenceSize / 100f, heights[k])).ToArray();
        uint[] indices =
        [
            0, 1, 2, 0, 2, 3, // first quad, at the bottom
            1, 4, 5, 1, 5, 2, // second
            2, 5, 6, 2, 6, 7, // third
            9, 2, 7, 9, 7, 8, // last, ending 1 m up over the first quad's left corner
        ];

        // A corner on a texel line touches the texels above and right of it, so row 20 is the first both touch. At
        // texel (10, 20), triangle 1 comes nearest its middle on the floor at (10.5, 20), and triangle 6 has its middle
        // inside, where the ramp has risen to 0.988 m.
        Assert.Equal(
            "Triangles 1 and 6 of one chart share texel (10, 20) but are 0.988 m apart.",
            LightmapUnwrap.Problem(positions, uv, indices));
    }

    [Fact]
    public void Names_charts_on_texel_lines_with_no_margin()
    {
        // Two 10-texel quads 2 texels apart, but with their edges on texel lines, as a chart rectangle with no margin
        // would place them. An edge on a texel line touches the texel past it, so the first quad's right edge at 20
        // touches texel 20, the second starts at texel 22, and only texel 21 lies empty between them.
        Vector3[] positions =
        [
            new(0f, 0f, 0f), new(1f, 0f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f),
            new(0f, 0f, 5f), new(1f, 0f, 5f), new(1f, 1f, 5f), new(0f, 1f, 5f),
        ];
        Vector2[] uvs =
        [
            Texels(10f, 10f), Texels(20f, 10f), Texels(20f, 20f), Texels(10f, 20f),
            Texels(22f, 10f), Texels(32f, 10f), Texels(32f, 20f), Texels(22f, 20f),
        ];
        Assert.Equal(
            "Triangles 0 and 2 are in different charts with only 1 empty texel between them at texel (20, 10), less than the 2-texel gutter.",
            LightmapUnwrap.Problem(positions, uvs, [0u, 1u, 2u, 0u, 2u, 3u, 4u, 5u, 6u, 4u, 6u, 7u]));
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

    [Fact]
    public void Measures_the_gutter_at_the_size_it_is_given()
    {
        // At 1024 texels the quads cover texels 20 to 39 and 43 to 62 across, with three empty between. At 512 they
        // halve to 10.25–19.75 and 21.75–31.25, texels 10 to 19 and 21 to 31, so only texel 20 lies between.
        Vector3[] positions =
        [
            new(0f, 0f, 0f), new(1f, 0f, 0f), new(1f, 1f, 0f), new(0f, 1f, 0f),
            new(0f, 0f, 5f), new(1f, 0f, 5f), new(1f, 1f, 5f), new(0f, 1f, 5f),
        ];
        Vector2[] uvs =
        [
            new Vector2(20.5f, 20.5f) / 1024f, new Vector2(39.5f, 20.5f) / 1024f, new Vector2(39.5f, 39.5f) / 1024f, new Vector2(20.5f, 39.5f) / 1024f,
            new Vector2(43.5f, 20.5f) / 1024f, new Vector2(62.5f, 20.5f) / 1024f, new Vector2(62.5f, 39.5f) / 1024f, new Vector2(43.5f, 39.5f) / 1024f,
        ];
        uint[] indices = [0u, 1u, 2u, 0u, 2u, 3u, 4u, 5u, 6u, 4u, 6u, 7u];

        Assert.Null(LightmapUnwrap.Problem(positions, uvs, indices, 1024));
        Assert.Equal(
            "Triangles 0 and 2 are in different charts with only 1 empty texel between them at texel (19, 10), less than the 2-texel gutter.",
            LightmapUnwrap.Problem(positions, uvs, indices, 512));
    }

    private static Vector2 Texels(float x, float y) => new Vector2(x, y) / LightmapUnwrap.ReferenceSize;
}
