namespace Super.Materials.Tests;

public class GeneratorTests
{
    public static TheoryData<string, Func<Image>> SmoothGenerators => new()
    {
        { "Perlin", () => Noise.Perlin(4, octaves: 4) },
        { "Cells F1", () => Noise.Cells(5) },
        { "Cells Edge", () => Noise.Cells(5, CellOutput.Edge) },
        { "Cells Id", () => Noise.Cells(5, CellOutput.Id) },
        { "Bricks", () => Tiles.Bricks(4, 8) },
        { "Grid", () => Tiles.Grid(3, 3) },
    };

    public static TheoryData<string, Func<Image>> Generators
    {
        get
        {
            var all = new TheoryData<string, Func<Image>>();

            foreach (var row in SmoothGenerators)
            {
                // Id jumps at cell borders, so whether the seam is rougher than the inside is down to the seed.
                // Cells_id_wraps_at_the_edges checks its wrap exactly instead.
                if ((string)row[0] != "Cells Id")
                {
                    all.Add((string)row[0], (Func<Image>)row[1]);
                }
            }

            all.Add("White", () => Noise.White());

            return all;
        }
    }

    // The name is only there so a failure says which generator it was.
#pragma warning disable xUnit1026
    [Theory]
    [MemberData(nameof(Generators))]
    public void Every_generator_tiles(string name, Func<Image> build) => NodeChecks.Tiles(build);

    [Theory]
    [MemberData(nameof(SmoothGenerators))]
    public void Smooth_generators_look_the_same_at_two_sizes(string name, Func<Image> build) => NodeChecks.SameAtSizes(build);
#pragma warning restore xUnit1026

    [Fact]
    public void Perlin_uses_the_whole_range_and_stays_in_it()
    {
        var p = GraphRunner.Evaluate(() => Noise.Perlin(4, octaves: 4), 256);
        Assert.All(p, v => Assert.InRange(v, 0f, 1f));
        Assert.InRange(p.Average(), 0.45f, 0.55f);
        Assert.True(p.Min() < 0.2f && p.Max() > 0.8f);
    }

    [Fact]
    public void Cells_without_jitter_put_one_point_in_the_middle_of_each_cell()
    {
        // Size 64, scale 1: pixel (31, 31) has uv (0.5 - 1/128) on both axes, and pixel (0, 0) is 0.5 - 1/128 from the middle.
        var p = GraphRunner.Evaluate(() => Noise.Cells(1, jitter: 0f), 64);
        Assert.Equal(MathF.Sqrt(2f) / 128f, p[31 * 64 + 31], 4);
        Assert.Equal(MathF.Sqrt(2f) * (0.5f - 1f / 128f), p[0], 4);
    }

    [Fact]
    public void Bricks_are_raised_with_gaps_between_and_alternate_rows_shifted()
    {
        // 2 columns, 2 rows at size 64: bricks are 32 by 32. A gap of 0.25 is 8 pixels split around each seam.
        var p = GraphRunner.Evaluate(() => Tiles.Bricks(2, 2, gap: 0.25f, bevel: 0f, offset: 0.5f), 64);
        Assert.Equal(1f, p[16 * 64 + 16]);  // middle of the first brick in row 0
        Assert.Equal(0f, p[16 * 64 + 0]);   // the seam at x = 0 in row 0
        Assert.Equal(1f, p[48 * 64 + 0]);   // row 1 is shifted half a brick, so x = 0 is mid-brick
        Assert.Equal(0f, p[48 * 64 + 16]);  // and its seam is at x = 16
    }

    [Fact]
    public void A_grid_does_not_shift_its_rows()
    {
        var p = GraphRunner.Evaluate(() => Tiles.Grid(2, 2, gap: 0.25f, bevel: 0f), 64);
        Assert.Equal(0f, p[48 * 64 + 0]);
        Assert.Equal(1f, p[48 * 64 + 16]);
    }

    [Fact]
    public void Bevel_rises_from_the_gap_to_the_top()
    {
        // One brick, gap 0.2 (0.1 on each side), bevel 0.2: the slope runs from x = 0.1 to x = 0.3.
        var p = GraphRunner.Evaluate(() => Tiles.Grid(1, 1, gap: 0.2f, bevel: 0.2f), 64);
        var row = 32 * 64;
        Assert.Equal(0f, p[row + 3]);              // x = 3.5 / 64 = 0.055, in the gap
        Assert.Equal(0.4766f, p[row + 12], 3);     // x = 12.5 / 64 = 0.1953, so (0.1953 - 0.1) / 0.2 of the way up
        Assert.Equal(1f, p[row + 32]);             // the top
    }

    [Fact]
    public void Height_variation_lowers_some_bricks_and_never_raises_any()
    {
        var p = GraphRunner.Evaluate(() => Tiles.Grid(4, 4, gap: 0f, bevel: 0f, heightVariation: 0.5f), 64);
        Assert.All(p, v => Assert.InRange(v, 0.5f, 1f));
        Assert.True(p.Min() < 0.9f);
        Assert.Equal(16, p.Distinct().Count());
    }

    [Fact]
    public void A_node_given_its_own_seed_changes_without_changing_its_neighbours()
    {
        float[] Second(int seed) => GraphRunner.Evaluate(() => { Noise.Perlin(4); return Noise.Perlin(4, seed: seed); }, 32);
        float[] First(int seed) => GraphRunner.Evaluate(() => { var a = Noise.Perlin(4); Noise.Perlin(4, seed: seed); return a; }, 32);
        Assert.NotEqual(Second(1), Second(2));
        Assert.Equal(First(1), First(2));
    }

    [Fact]
    public void The_graph_seed_changes_perlin()
    {
        var a = GraphRunner.Evaluate(() => Noise.Perlin(4), 32, seed: 1);
        var b = GraphRunner.Evaluate(() => Noise.Perlin(4), 32, seed: 2);
        Assert.NotEqual(a, b);
        Assert.Equal(a, GraphRunner.Evaluate(() => Noise.Perlin(4), 32, seed: 1));
    }

    [Fact]
    public void White_noise_differs_per_pixel_and_stays_in_range()
    {
        var p = GraphRunner.Evaluate(() => Noise.White(), 32);
        Assert.All(p, v => Assert.InRange(v, 0f, 1f));
        Assert.True(p.Distinct().Count() > 1000);
    }

    [Fact]
    public void Cells_id_gives_each_cell_one_value()
    {
        // Scale 2 at size 32 with no jitter: points are at the middle of each 16 pixel cell, and Id is flat around each.
        var p = GraphRunner.Evaluate(() => Noise.Cells(2, CellOutput.Id, jitter: 0f), 32);
        Assert.Equal(p[8 * 32 + 8], p[7 * 32 + 7]);
        Assert.Equal(4, new[] { p[8 * 32 + 8], p[8 * 32 + 24], p[24 * 32 + 8], p[24 * 32 + 24] }.Distinct().Count());
    }

    [Fact]
    public void Cells_id_wraps_at_the_edges()
    {
        // 5 x 5 cells with full jitter at 16 pixels per cell. A pixel near an edge often has its nearest point in a
        // cell across the edge, and that cell's id must be the id of the cell on the far side, not a new value.
        var p = GraphRunner.Evaluate(() => Noise.Cells(5, CellOutput.Id), 80);
        Assert.True(p.Distinct().Count() <= 25, $"{p.Distinct().Count()} ids for 25 cells.");
    }

    [Fact]
    public void Cells_with_jitter_outside_zero_to_one_names_the_range()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Noise.Cells(4, jitter: 1.5f), 8));
        Assert.StartsWith("jitter must be from 0 to 1, not 1.5.", error.Message);
        Assert.Equal("jitter", error.ParamName);
    }

    [Fact]
    public void Bricks_that_would_not_tile_vertically_say_why()
    {
        var error = Assert.Throws<ArgumentException>(() => GraphRunner.Evaluate(() => Tiles.Bricks(4, 5), 8));
        Assert.Equal("offset × rows must be a whole number so the bricks tile, not 0.5 × 5 = 2.5. Use an even number of rows, or an offset of 0 or 1.", error.Message);
    }

    [Fact]
    public void Odd_rows_are_fine_without_an_offset_or_with_a_whole_one()
    {
        GraphRunner.Evaluate(() => Tiles.Grid(4, 5), 8);
        GraphRunner.Evaluate(() => Tiles.Bricks(4, 5, offset: 1f), 8);
        GraphRunner.Evaluate(() => Tiles.Bricks(4, 6, offset: 0.5f), 8);
    }

    [Fact]
    public void Cells_edge_is_zero_between_two_points_and_grows_towards_a_point()
    {
        // Scale 2, no jitter, size 32: points at 8 and 24 pixels along row 8. F2 - F1 is 0 half way between them.
        var p = GraphRunner.Evaluate(() => Noise.Cells(2, CellOutput.Edge, jitter: 0f), 32);
        // Pixel (15, 8): F1 = sqrt(7.5^2 + 0.5^2) / 16 and F2 = sqrt(8.5^2 + 0.5^2) / 16, which is 0.0624 apart.
        Assert.Equal(0.0624f, p[8 * 32 + 15], 3);
        // Pixel (8, 8): F1 = sqrt(0.5^2 + 0.5^2) / 16 = 0.0442 and F2 = sqrt(15.5^2 + 0.5^2) / 16 = 0.9693.
        Assert.Equal(0.9251f, p[8 * 32 + 8], 3);
    }

    [Theory]
    [InlineData("perlin")]
    [InlineData("cells")]
    public void The_same_pixels_come_out_on_one_thread_and_many(string which)
    {
        Func<Image> build = which == "perlin" ? () => Noise.Perlin(4, octaves: 3) : () => Noise.Cells(6);
        Assert.Equal(GraphRunner.Evaluate(build, 64, maxThreads: 1), GraphRunner.Evaluate(build, 64, maxThreads: 8));
    }

    [Fact]
    public void A_scale_of_zero_names_the_parameter()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Noise.Perlin(0), 8));
        Assert.Equal("scale must be at least 1, not 0. (Parameter 'scale')", error.Message);
        Assert.Equal("scale", error.ParamName);
    }

    [Fact]
    public void Too_many_octaves_names_the_range()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Noise.Perlin(4, octaves: 13), 8));
        Assert.StartsWith("octaves must be from 1 to 12, not 13.", error.Message);
    }

    [Fact]
    public void Zero_octaves_names_the_range()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Noise.Perlin(4, octaves: 0), 8));
        Assert.StartsWith("octaves must be from 1 to 12, not 0.", error.Message);
    }

    [Fact]
    public void Cells_with_scale_zero_names_the_parameter()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Noise.Cells(0), 8));
        Assert.Equal("scale must be at least 1, not 0. (Parameter 'scale')", error.Message);
    }

    [Fact]
    public void Bricks_with_no_columns_or_rows_name_the_parameter()
    {
        var columns = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Tiles.Bricks(0, 2), 8));
        Assert.StartsWith("columns must be at least 1, not 0.", columns.Message);

        var rows = Assert.Throws<ArgumentOutOfRangeException>(() => GraphRunner.Evaluate(() => Tiles.Grid(2, -1), 8));
        Assert.StartsWith("rows must be at least 1, not -1.", rows.Message);
    }
}
