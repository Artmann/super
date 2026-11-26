using System.Globalization;
using System.Numerics;

namespace Super.Tests.Grids;

public sealed class DebugGridTests
{
    /// <summary>A frame's debug canvas with these channels shown.</summary>
    private static ViewDebug Showing(params string[] channels)
    {
        var shown = new DebugDraw(new GameTime(30));
        foreach (var channel in channels)
        {
            shown.Show(channel);
        }

        var view = new View();
        view.Debug.Channels = shown;
        return view.Debug;
    }

    private static string[] Sorted(IEnumerable<string> texts) => texts.Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Square_outlines_draw_four_sides_per_cell_meeting_on_the_cell_corners()
    {
        var debug = Showing("grid");
        debug.Grid(new SquareGrid(1f), new Cell(0, 0), 1);

        var lines = debug.Lines.ToArray();
        Assert.Equal(9 * 4, lines.Length);
        Assert.Equal(36, debug.Count("grid"));
        // Three cells each way from -1 to 1 have corners on every whole number from -1 to 2.
        var ends = lines.SelectMany(line => new[] { line.From, line.To }).Distinct().ToArray();
        Assert.Equal(16, ends.Length);
        Assert.All(ends, end =>
        {
            Assert.Contains(end.X, new[] { -1f, 0f, 1f, 2f });
            Assert.Contains(end.Z, new[] { -1f, 0f, 1f, 2f });
            Assert.Equal(0f, end.Y);
        });
    }

    [Fact]
    public void Hex_outlines_cover_the_big_hexagon_two_steps_out()
    {
        var debug = Showing("grid");
        debug.Grid(new HexGrid(1f), new Hex(0, 0), 2);

        // 3 * 2 * 3 + 1 hexes, six sides each.
        Assert.Equal(19 * 6, debug.Lines.Length);
    }

    [Fact]
    public void Outlines_follow_a_raised_grid()
    {
        var debug = Showing("grid");
        var raised = new SquareGrid(1f, new GridPlacement(new Vector3(0f, 3f, 0f)));
        debug.Grid(raised, new Cell(4, 4), 2);

        Assert.All(debug.Lines.ToArray(), line =>
        {
            Assert.Equal(3f, line.From.Y, 4);
            Assert.Equal(3f, line.To.Y, 4);
        });
    }

    [Fact]
    public void A_hidden_channel_draws_nothing_and_asks_nothing()
    {
        var debug = Showing();
        int asked = 0;
        debug.Grid(new SquareGrid(1f), default, 3);
        debug.GridLabels(new SquareGrid(1f), default, 3);
        debug.GridHeat(new SquareGrid(1f), default, 3, _ => asked++, new HeatScale(0f, 1f));
        debug.Cells(new SquareGrid(1f), [new Cell(0, 0)], Color.Red, "path");

        Assert.Equal(0, debug.Lines.Length);
        Assert.Equal(0, debug.Labels.Length);
        Assert.Equal(0, asked);
    }

    [Fact]
    public void Each_overlay_draws_in_its_own_channel_unless_given_one()
    {
        var debug = Showing("grid", "grid.labels", "grid.heat", "grid.heat:cost");
        var grid = new SquareGrid(1f);
        debug.Grid(grid, default, 0);
        debug.GridLabels(grid, default, 0);
        debug.GridHeat(grid, default, 0, _ => 1f, new HeatScale(0f, 1f));
        debug.GridHeat(grid, default, 0, _ => 1f, new HeatScale(0f, 1f), "grid.heat:cost");

        Assert.Equal(4, debug.Count("grid"));
        Assert.Equal("grid.labels", Assert.Single(debug.Labels.ToArray(), label => label.Text == "0, 0").Channel);
        // Five rings of four sides, then a legend of eight pieces.
        Assert.Equal(5 * 4 + 8, debug.Count("grid.heat"));
        Assert.Equal(5 * 4 + 8, debug.Count("grid.heat:cost"));
    }

    [Fact]
    public void Labels_give_each_cells_coordinates_over_its_middle_whatever_the_culture()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var debug = Showing("grid.labels");
            debug.GridLabels(new SquareGrid(2f), new Cell(-1, 0), 1);

            var labels = debug.Labels.ToArray();
            Assert.Equal(
                Sorted(["-2, -1", "-1, -1", "0, -1", "-2, 0", "-1, 0", "0, 0", "-2, 1", "-1, 1", "0, 1"]),
                Sorted(labels.Select(label => label.Text)));
            // Cell -2, 1 on 2 m cells runs from -4 to -2 along X and 2 to 4 along Z.
            Assert.Equal(new Vector3(-3f, 0f, 3f), Assert.Single(labels, label => label.Text == "-2, 1").Position);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Hex_labels_cover_the_same_hexes_as_range_round_any_centre()
    {
        var debug = Showing("grid.labels");
        var center = new Hex(3, -5);
        debug.GridLabels(new HexGrid(1f), center, 2);

        Assert.Equal(
            Sorted(Hex.Range(center, 2).Select(hex => $"{hex.Q}, {hex.R}")),
            Sorted(debug.Labels.ToArray().Select(label => label.Text)));
    }

    [Fact]
    public void Heat_colours_cells_on_the_scale_and_leaves_out_cells_with_no_value()
    {
        var debug = Showing("grid.heat");
        var grid = new SquareGrid(1f);
        debug.GridHeat(grid, default, 1, cell => cell.X == 0 ? null : cell.X, new HeatScale(0f, 2f));

        var lines = debug.Lines.ToArray();
        Assert.Equal(6 * 5 * 4 + 8, lines.Length);
        // Nothing round the middle of the cells in column 0, which have no value.
        Assert.DoesNotContain(lines, line => line.From.X > 0f && line.From.X < 1f && line.Look.Thickness == 0f);
        // Column -1 is below the scale, so all blue, and column 1 is halfway, so half red and half blue.
        Assert.Contains(lines, line => line.From.X < 0f && line.Color == Color.Blue);
        Assert.Contains(lines, line => line.From.X > 1f && line.Color == new Color(0.5f, 0f, 0.5f));
    }

    [Fact]
    public void Heat_leaves_out_a_cell_whose_value_isnt_a_number()
    {
        var debug = Showing("grid.heat");
        debug.GridHeat(new SquareGrid(1f), default, 0, _ => float.NaN, new HeatScale(0f, 1f));

        // Only the legend.
        Assert.Equal(8, debug.Lines.Length);
    }

    [Fact]
    public void Heat_has_a_legend_from_cold_to_hot_with_its_numbers()
    {
        var debug = Showing("grid.heat");
        debug.GridHeat(new SquareGrid(1f), default, 1, _ => 0f, new HeatScale(0f, 2.5f) { Cold = Color.Green, Hot = Color.Yellow });

        var labels = debug.Labels.ToArray();
        Assert.Equal(2, labels.Length);
        Assert.Equal(("0", Color.Green), (labels[0].Text, labels[0].Color));
        Assert.Equal(("2.5", Color.Yellow), (labels[1].Text, labels[1].Color));
        // A step past the last cell's middle at 1.5, so half a cell past the area's edge at 2, and three cells long
        // across Z round the middle row at 0.5.
        Assert.Equal(new Vector3(2.5f, 0f, 2f), labels[0].Position);
        Assert.Equal(new Vector3(2.5f, 0f, -1f), labels[1].Position);
        var bar = debug.Lines.ToArray().Where(line => line.Look.Thickness == 6f).ToArray();
        Assert.Equal(8, bar.Length);
    }

    [Fact]
    public void Heat_with_no_scale_runs_from_the_lowest_value_to_the_highest()
    {
        var debug = Showing("grid.heat");
        // Columns -1, 0 and 1 give 2, 4 and 6, and an endless value doesn't stretch the scale.
        debug.GridHeat(new SquareGrid(1f), default, 1, cell => cell == new Cell(0, 1) ? float.PositiveInfinity : cell.X * 2f + 4f);

        var labels = debug.Labels.ToArray();
        Assert.Equal(["2", "6"], labels.Select(label => label.Text));
        Assert.Contains(debug.Lines.ToArray(), line => line.From.X > 0f && line.From.X < 1f && line.Color == new Color(0.5f, 0f, 0.5f));
        Assert.Contains(debug.Lines.ToArray(), line => line.From.X > 1f && line.Color == Color.Red);
    }

    [Theory]
    [InlineData(3f, "3", "4")]
    [InlineData(null, "0", "1")]
    public void Heat_with_no_scale_and_no_spread_is_one_wide(float? every, string low, string high)
    {
        var debug = Showing("grid.heat");
        debug.GridHeat(new SquareGrid(1f), default, 1, _ => every);

        Assert.Equal([low, high], debug.Labels.ToArray().Select(label => label.Text));
    }

    [Fact]
    public void A_heat_scale_blends_and_stops_at_its_ends()
    {
        var scale = new HeatScale(2f, 6f);

        Assert.Equal(new Color(0.25f, 0f, 0.75f), scale.At(3f));
        Assert.Equal(Color.Blue, scale.At(-10f));
        Assert.Equal(Color.Red, scale.At(10f));
    }

    [Theory]
    [InlineData(1f, 1f)]
    [InlineData(2f, 1f)]
    [InlineData(float.NaN, 1f)]
    public void A_heat_scale_that_doesnt_go_up_says_so(float low, float high)
    {
        var debug = Showing("grid.heat");
        var error = Assert.Throws<ArgumentException>(
            () => debug.GridHeat(new SquareGrid(1f), default, 1, _ => 0f, new HeatScale(low, high)));
        Assert.StartsWith("A heat scale runs from a low number up to a higher one, but this one runs from", error.Message);
    }

    [Fact]
    public void Highlights_sit_inside_each_cells_outline()
    {
        var debug = Showing("path");
        debug.Cells(new SquareGrid(1f), [new Cell(0, 0), new Cell(1, 0)], Color.Red, "path");

        var lines = debug.Lines.ToArray();
        Assert.Equal(2 * 2 * 4, lines.Length);
        // The outer ring is 0.86 of the cell, so its corner nearest the origin is 0.07 in.
        Assert.Contains(lines, line => Vector3.Distance(line.From, new Vector3(0.07f, 0f, 0.07f)) < 1e-4f);
        Assert.All(lines, line => Assert.True(line.From.Z > 0f && line.From.Z < 1f));
    }

    [Fact]
    public void Highlights_take_any_cells_such_as_a_flood_fill()
    {
        var debug = Showing("range");
        var reach = Hex.Reachable(default, 1f, _ => 1f);
        debug.Cells(new HexGrid(1f), reach.Keys, Color.Green, "range");

        Assert.Equal(7 * 2 * 6, debug.Count("range"));
    }

    [Fact]
    public void A_negative_area_throws()
    {
        var debug = Showing("grid");
        Assert.Throws<ArgumentOutOfRangeException>(() => debug.Grid(new SquareGrid(1f), default, -1));
    }

    [Fact]
    public void Drawing_every_overlay_allocates_nothing_once_warm()
    {
        var debug = Showing("grid", "grid.labels", "grid.heat", "path");
        var grid = new HexGrid(1f);
        Hex[] path = [new(0, 0), new(1, 0)];
        var scale = new HeatScale(0f, 4f);

        void Once()
        {
            debug.Clear();
            debug.Grid(grid, default, 3);
            debug.GridLabels(grid, default, 3);
            debug.GridHeat(grid, default, 3, static hex => hex.Q, scale);
            debug.Cells(grid, path, Color.Red, "path");
        }

        for (int i = 0; i < 5; i++)
        {
            Once();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 50; i++)
        {
            Once();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
