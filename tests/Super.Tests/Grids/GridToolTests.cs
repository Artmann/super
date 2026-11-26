using System.Globalization;
using System.Numerics;
using Super.Testing;

namespace Super.Tests.Grids;

public sealed class GridToolTests
{
    [Theory]
    [InlineData(HexTop.Pointy, HexOffset.Odd, 0, 1, 0, 1)]
    [InlineData(HexTop.Pointy, HexOffset.Odd, -1, 3, 0, 3)]
    [InlineData(HexTop.Pointy, HexOffset.Even, -1, 1, 0, 1)]
    [InlineData(HexTop.Pointy, HexOffset.Odd, 1, -1, 0, -1)]
    [InlineData(HexTop.Flat, HexOffset.Odd, 1, 0, 1, 0)]
    [InlineData(HexTop.Flat, HexOffset.Even, 1, -1, 1, 0)]
    [InlineData(HexTop.Flat, HexOffset.Odd, 2, -1, 2, 0)]
    public void Offset_coordinates_match_hand_worked_hexes(HexTop top, HexOffset pushed, int q, int r, int column, int row)
    {
        var grid = new HexGrid(1f, top);

        Assert.Equal((column, row), grid.ToOffset(new Hex(q, r), pushed));
        Assert.Equal(new Hex(q, r), grid.FromOffset(column, row, pushed));
    }

    [Theory]
    [InlineData(HexTop.Pointy, HexOffset.Odd)]
    [InlineData(HexTop.Pointy, HexOffset.Even)]
    [InlineData(HexTop.Flat, HexOffset.Odd)]
    [InlineData(HexTop.Flat, HexOffset.Even)]
    public void Offset_column_zero_zigzags_by_half_a_hex(HexTop top, HexOffset pushed)
    {
        // Pointy rows are √3 apart along X, flat columns √3 apart along Z, so half a hex is √3 / 2. Odd pushes the odd
        // lines out by that; even leaves the even lines out by that, which pulls the odd ones back.
        var grid = new HexGrid(1f, top);
        float half = MathF.Sqrt(3f) / 2f * (pushed == HexOffset.Odd ? 1f : -1f);
        for (int line = -3; line <= 3; line++)
        {
            var middle = grid.Center(top == HexTop.Pointy ? grid.FromOffset(0, line, pushed) : grid.FromOffset(line, 0, pushed));

            Assert.Equal((line & 1) * half, top == HexTop.Pointy ? middle.X : middle.Z, 1e-4f);
        }
    }

    [Fact]
    public void A_cell_mirrors_across_another_cells_column()
    {
        Assert.Equal(new Cell(1, 4), Cell.Mirror(new Cell(5, 4), new Cell(3, 0)));
    }

    [Theory]
    [InlineData(HexTop.Pointy)]
    [InlineData(HexTop.Flat)]
    public void A_mirrored_hex_sits_where_the_world_flips_it(HexTop top)
    {
        var grid = new HexGrid(1f, top);
        var around = new Hex(1, -2);
        foreach (var hex in Hex.Range(around, 3))
        {
            var mirrored = grid.Mirror(hex, around);
            var expected = grid.Center(hex);
            expected.X = 2f * grid.Center(around).X - expected.X;

            Assert.True(Vector3.Distance(expected, grid.Center(mirrored)) < 1e-4f, $"{hex} mirrored to {mirrored}.");
        }
    }

    [Fact]
    public void A_walk_goes_round_a_wall_and_stops_where_the_budget_runs_out()
    {
        // A wall along x = 1 from z = -1 to z = 1, and swamp at (0, 2) costing 3.
        float? Cost(Cell cell) => cell.X == 1 && Math.Abs(cell.Z) <= 1 ? null : cell == new Cell(0, 2) ? 3f : 1f;

        var reach = Cell.Reachable(new Cell(0, 0), 4f, Cost);

        Assert.Equal(0f, reach[new Cell(0, 0)]);
        Assert.False(reach.ContainsKey(new Cell(1, 0)));
        Assert.Equal(4f, reach[new Cell(0, 2)]);
        // Round the wall's end: (0,-1), (0,-2), (1,-2), (2,-2), then up to (2,-1) is 5, over budget.
        Assert.Equal(4f, reach[new Cell(2, -2)]);
        Assert.False(reach.ContainsKey(new Cell(2, -1)));
        Assert.Equal(4f, reach[new Cell(-4, 0)]);
        Assert.False(reach.ContainsKey(new Cell(-5, 0)));
    }

    [Fact]
    public void A_walk_across_corners_reaches_a_square_of_cells()
    {
        var reach = Cell.Reachable(new Cell(0, 0), 2f, _ => 1f, corners: true);

        Assert.Equal(25, reach.Count);
        Assert.Equal(2f, reach[new Cell(2, -2)]);
    }

    [Theory, Seeds(15)]
    public void A_free_hex_walk_reaches_the_hexes_within_its_budget(int seed)
    {
        var random = new Random(seed);
        var start = new Hex(random.Next(-20, 20), random.Next(-20, 20));
        int steps = random.Next(0, 6);

        var reach = Hex.Reachable(start, steps, _ => 1f);

        Assert.Equal(1 + 3 * steps * (steps + 1), reach.Count);
        Assert.All(reach, pair => Assert.Equal((float)Hex.Distance(start, pair.Key), pair.Value));
    }

    [Fact]
    public void A_negative_step_cost_throws()
    {
        var error = Assert.Throws<ArgumentException>(() => Hex.Reachable(default, 3f, _ => -1f));

        Assert.StartsWith("Stepping into Hex { Q = 1, R = 0, S = -1 } costs -1, but a step must cost 0 or more.", error.Message);
    }

    [Fact]
    public void A_footprint_turns_round_its_own_first_cell()
    {
        var barn = Footprints.Rectangle(2, 3);
        Span<Cell> cells = stackalloc Cell[barn.Count];

        barn.Placed(new Cell(10, 10), 1, cells);

        // A quarter turn takes +X to −Z and +Z to +X.
        Assert.Equal(
            [new Cell(10, 9), new Cell(10, 10), new Cell(11, 9), new Cell(11, 10), new Cell(12, 9), new Cell(12, 10)],
            cells.ToArray().OrderBy(cell => cell.X).ThenBy(cell => cell.Z));
    }

    [Theory, Seeds(15)]
    public void A_turned_footprint_keeps_its_cells_apart(int seed)
    {
        var random = new Random(seed);
        var temple = Footprints.Of(new Hex(0, 0), new Hex(1, 0), new Hex(0, 1), new Hex(random.Next(2, 5), -1));
        Span<Hex> placed = stackalloc Hex[temple.Count];

        temple.Placed(new Hex(random.Next(-9, 9), random.Next(-9, 9)), random.Next(-12, 12), placed);

        Assert.Equal(temple.Count, placed.ToArray().Distinct().Count());
    }

    [Fact]
    public void A_footprint_fits_only_where_every_cell_is_free()
    {
        var barn = Footprints.Rectangle(2, 2);
        var rock = new Cell(5, 6);

        Assert.True(barn.Fits(new Cell(5, 4), 0, cell => cell != rock));
        Assert.False(barn.Fits(new Cell(5, 5), 0, cell => cell != rock));
        // Turned a quarter, it covers z 5 and 4 instead, so it fits.
        Assert.True(barn.Fits(new Cell(5, 5), 1, cell => cell != rock));
    }

    [Fact]
    public void A_footprint_with_a_cell_twice_throws()
    {
        var error = Assert.Throws<ArgumentException>(() => Footprints.Of(new Cell(0, 0), new Cell(0, 0)));

        Assert.StartsWith("A footprint lists each cell once, but Cell { X = 0, Z = 0 } is there twice.", error.Message);
    }

    [Fact]
    public void A_mirrored_footprint_reaches_the_other_way()
    {
        var wing = Footprints.Of(new Cell(0, 0), new Cell(1, 0), new Cell(1, 1));

        var mirrored = wing.Mirrored(new SquareGrid(1f));

        Assert.Equal([new Cell(0, 0), new Cell(-1, 0), new Cell(-1, 1)], mirrored.Cells.ToArray());
    }

    [Fact]
    public void A_grid_map_keeps_cells_on_both_sides_of_zero_in_separate_chunks()
    {
        var map = new GridMap<Cell, string>();

        map.Set(new Cell(-1, 0), "west");
        map.Set(new Cell(0, 0), "east");
        map[new Cell(0, 0)] = "middle";

        Assert.Equal(2, map.Count);
        Assert.Equal("west", map[new Cell(-1, 0)]);
        Assert.Equal("middle", map[new Cell(0, 0)]);
        Assert.Equal(new GridChunk(-1, 0), GridMap<Cell, string>.ChunkOf(new Cell(-1, 0)));
        Assert.Equal(new GridChunk(0, 0), GridMap<Cell, string>.ChunkOf(new Cell(15, 15)));
        Assert.Equal(new GridChunk(-1, 1), GridMap<Cell, string>.ChunkOf(new Cell(-16, 16)));
        Assert.Equal([new GridChunk(-1, 0), new GridChunk(0, 0)], map.Chunks.OrderBy(chunk => chunk.A));
    }

    [Fact]
    public void Reading_an_empty_cell_of_a_grid_map_throws()
    {
        var map = new GridMap<Hex, int>();

        var error = Assert.Throws<KeyNotFoundException>(() => map[new Hex(2, 3)]);

        Assert.Equal("Hex { Q = 2, R = 3, S = -5 } holds nothing.", error.Message);
    }

    [Fact]
    public void Removing_the_last_cell_of_a_chunk_drops_the_chunk()
    {
        var map = new GridMap<Cell, int>();
        map.Set(new Cell(3, 3), 1);

        Assert.True(map.Remove(new Cell(3, 3)));
        Assert.False(map.Remove(new Cell(3, 3)));

        Assert.Equal(0, map.Count);
        Assert.Empty(map.Chunks);
        Assert.Equal(-1, map.ChunkVersion(new GridChunk(0, 0)));
    }

    [Fact]
    public void Changing_a_cell_changes_only_its_own_chunks_version()
    {
        var map = new GridMap<Cell, int>();
        map.Set(new Cell(0, 0), 1);
        map.Set(new Cell(20, 0), 2);
        long near = map.ChunkVersion(new GridChunk(0, 0));
        long far = map.ChunkVersion(new GridChunk(1, 0));

        map.Set(new Cell(1, 1), 3);

        Assert.NotEqual(near, map.ChunkVersion(new GridChunk(0, 0)));
        Assert.Equal(far, map.ChunkVersion(new GridChunk(1, 0)));
    }

    [Theory, Seeds(15)]
    public void A_grid_map_lists_every_cell_it_was_given(int seed)
    {
        var random = new Random(seed);
        var expected = new Dictionary<Hex, int>();
        var map = new GridMap<Hex, int>();
        for (int i = 0; i < 200; i++)
        {
            var hex = new Hex(random.Next(-40, 40), random.Next(-40, 40));
            expected[hex] = i;
            map.Set(hex, i);
        }

        foreach (var hex in expected.Keys.Take(50).ToArray())
        {
            expected.Remove(hex);
            map.Remove(hex);
        }

        Assert.Equal(expected.Count, map.Count);
        Assert.Equal(expected.OrderBy(pair => pair.Key.Q).ThenBy(pair => pair.Key.R), map.OrderBy(pair => pair.Key.Q).ThenBy(pair => pair.Key.R));
    }

    [Fact]
    public void A_grid_map_saves_row_by_row_and_loads_back()
    {
        var map = new GridMap<Cell, Tile>();
        map.Set(new Cell(4, 1), new Tile("wall", 2));
        map.Set(new Cell(-3, 0), new Tile("floor", 0));

        string json = GridMaps.ToJson(map);
        var loaded = GridMaps.FromJson<Cell, Tile>(json);

        Assert.Equal(
            """{"cells":[{"at":[-3,0],"value":{"name":"floor","turns":0}},{"at":[4,1],"value":{"name":"wall","turns":2}}]}""",
            json.Replace(" ", "").Replace("\r", "").Replace("\n", ""));
        Assert.Equal(2, loaded.Count);
        Assert.Equal(new Tile("wall", 2), loaded[new Cell(4, 1)]);
    }

    [Fact]
    public void Loading_a_grid_map_with_a_cell_twice_throws()
    {
        string json = """{ "cells": [ { "at": [1, 2], "value": 1 }, { "at": [1, 2], "value": 2 } ] }""";

        var error = Assert.Throws<System.Text.Json.JsonException>(() => GridMaps.FromJson<Hex, int>(json));

        Assert.Equal("Cell 1 of the grid map is at Hex { Q = 1, R = 2, S = -3 }, but an earlier cell is there too.", error.Message);
    }

    [Fact]
    public void Loading_a_grid_map_cell_without_two_numbers_throws()
    {
        string json = """{ "cells": [ { "at": [1], "value": 1 } ] }""";

        var error = Assert.Throws<System.Text.Json.JsonException>(() => GridMaps.FromJson<Cell, int>(json));

        Assert.Equal("Cell 0 of the grid map needs \"at\" as two whole numbers, such as [3, -2].", error.Message);
    }

    [Fact]
    public void The_nearest_grid_under_a_ray_wins_unless_it_doesnt_cover_the_spot()
    {
        var grids = new global::Super.Grids();
        grids.Add("ground", new SquareGrid(1f));
        grids.Add("deck", new HexGrid(1f, placement: new GridPlacement(new Vector3(0f, 5f, 0f))), hex => Hex.Distance(hex, default) <= 2);
        var down = new Vector3(0f, -1f, 0f);

        var overDeck = grids.Under(new Ray(new Vector3(0.2f, 10f, 0.1f), down));
        var pastDeck = grids.Under(new Ray(new Vector3(20.5f, 10f, 0.5f), down));

        Assert.Equal("deck", overDeck!.Value.Name);
        Assert.Equal(5f, overDeck.Value.Distance, 1e-4f);
        Assert.Equal(new Hex(0, 0), overDeck.Value.Cell<Hex>());
        Assert.Equal("ground", pastDeck!.Value.Name);
        Assert.Equal(new Cell(20, 0), pastDeck.Value.Cell<Cell>());
        Assert.Null(grids.Under(new Ray(new Vector3(0f, 10f, 0f), -down)));
    }

    [Fact]
    public void Asking_for_a_grid_by_the_wrong_cell_or_name_throws()
    {
        var grids = new global::Super.Grids();
        grids.Add("build", new SquareGrid(2f));

        var wrongCell = Assert.Throws<InvalidOperationException>(() => grids.Get<Hex>("build"));
        var wrongName = Assert.Throws<KeyNotFoundException>(() => grids.Get<Cell>("farm"));
        var twice = Assert.Throws<ArgumentException>(() => grids.Add("build", new SquareGrid(1f)));

        Assert.Equal("Grid build is a SquareGrid, which has no Hex cells.", wrongCell.Message);
        Assert.Equal("There is no grid called farm. There are: build.", wrongName.Message);
        Assert.StartsWith("There is already a grid called build. Remove it first to replace it.", twice.Message);
    }

    [Fact]
    public void A_layered_grid_finds_the_floor_a_point_stands_on()
    {
        var floors = new LayeredGrid<Cell>(new SquareGrid(1f, new GridPlacement(new Vector3(0f, 10f, 0f))), 0f, 3f, 6f);

        Assert.Equal((0, new Cell(2, 2)), floors.CellAt(new Vector3(2.5f, 12f, 2.5f)));
        Assert.Equal((1, new Cell(2, 2)), floors.CellAt(new Vector3(2.5f, 12.995f, 2.5f)));
        Assert.Equal(2, floors.LayerAt(new Vector3(0f, 100f, 0f)));
        Assert.Equal(0, floors.LayerAt(new Vector3(0f, -100f, 0f)));
        Assert.Equal(16f, floors[2].Center(default).Y, 1e-4f);
    }

    [Fact]
    public void Moving_a_layered_grid_moves_every_layer()
    {
        var floors = new LayeredGrid<Hex>(new HexGrid(1f), 0f, 4f);
        var tilted = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 2f);

        floors.Placement = new GridPlacement(new Vector3(1f, 2f, 3f), tilted);

        // Turned a quarter about X, the grid's up points along +Z.
        Assert.True(Vector3.Distance(new Vector3(1f, 2f, 7f), floors[1].Placement.Origin) < 1e-4f, $"Layer 1 is at {floors[1].Placement.Origin}.");
        Assert.Equal(tilted, floors[1].Placement.Rotation);
    }

    [Fact]
    public void Layer_heights_that_dont_go_up_throw()
    {
        var error = Assert.Throws<ArgumentException>(() => new LayeredGrid<Cell>(new SquareGrid(1f), 0f, 3f, 3f));

        Assert.StartsWith("A layered grid's heights must be numbers that go up, but height 2 is 3.", error.Message);
    }

    [Fact]
    public void Layer_height_errors_read_the_same_in_any_culture()
    {
        var before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        try
        {
            var error = Assert.Throws<ArgumentException>(() => new LayeredGrid<Cell>(new SquareGrid(1f), 0.5f, 0.25f));

            Assert.StartsWith("A layered grid's heights must be numbers that go up, but height 1 is 0.25.", error.Message);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    private sealed record Tile(string Name, int Turns);
}
