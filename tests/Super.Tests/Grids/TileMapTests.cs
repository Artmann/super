using System.Numerics;
using Super.Testing;

namespace Super.Tests.Grids;

public sealed class TileMapTests
{
    private const string Walls = "walls";

    // Cell.Sides goes +X, +Z, −X, −Z, and one turn moves +X to −Z.
    private static readonly Tile[] WallTiles =
    [
        new("post", ModelRef.Cube, Walls, "0000"),
        new("end", ModelRef.Cube, Walls, "1000"),
        new("straight", ModelRef.Cube, Walls, "1010"),
        new("corner", ModelRef.Cube, Walls, "1100"),
        new("tee", ModelRef.Cube, Walls, "1110"),
        new("cross", ModelRef.Cube, Walls, "1111"),
        new("floor", ModelRef.Plane, "floor"),
        new("statue", ModelRef.Sphere),
    ];

    private static TileMap<Cell> Square(IEnumerable<Tile>? tiles = null) =>
        new(new TileSet<Cell>(tiles ?? WallTiles), new SquareGrid(1f));

    private static TileMap<Cell> Painted(params Cell[] cells)
    {
        var map = Square();
        map.Paint(cells, Walls);
        return map;
    }

    [Fact]
    public void A_wall_on_its_own_is_a_post()
    {
        Assert.Equal(new PlacedTile("post"), Painted(new Cell(0, 0)).Cells()[new Cell(0, 0)]);
    }

    [Fact]
    public void Two_walls_side_by_side_are_ends_facing_each_other()
    {
        var map = Painted(new Cell(0, 0), new Cell(1, 0));

        Assert.Equal(new PlacedTile("end", 0), map.Cells()[new Cell(0, 0)]);
        Assert.Equal(new PlacedTile("end", 2), map.Cells()[new Cell(1, 0)]);
    }

    [Fact]
    public void A_wall_running_along_z_is_a_straight_turned_once()
    {
        var map = Painted(new Cell(0, 0), new Cell(0, 1), new Cell(0, 2));

        Assert.Equal(new PlacedTile("straight", 1), map.Cells()[new Cell(0, 1)]);
        Assert.Equal(new PlacedTile("end", 3), map.Cells()[new Cell(0, 0)]);
        Assert.Equal(new PlacedTile("end", 1), map.Cells()[new Cell(0, 2)]);
    }

    [Fact]
    public void Corners_turn_to_face_both_their_walls()
    {
        var map = Painted(new Cell(0, 0), new Cell(1, 0), new Cell(0, 1), new Cell(1, 1));

        Assert.Equal(new PlacedTile("corner", 0), map.Cells()[new Cell(0, 0)]);
        Assert.Equal(new PlacedTile("corner", 1), map.Cells()[new Cell(0, 1)]);
        Assert.Equal(new PlacedTile("corner", 2), map.Cells()[new Cell(1, 1)]);
        Assert.Equal(new PlacedTile("corner", 3), map.Cells()[new Cell(1, 0)]);
    }

    [Fact]
    public void Three_walls_meeting_make_a_tee_and_four_a_cross()
    {
        var tee = Painted(new Cell(0, 0), new Cell(1, 0), new Cell(-1, 0), new Cell(0, -1));
        var cross = Painted(new Cell(0, 0), new Cell(1, 0), new Cell(-1, 0), new Cell(0, 1), new Cell(0, -1));

        Assert.Equal(new PlacedTile("tee", 2), tee.Cells()[new Cell(0, 0)]);
        Assert.Equal(new PlacedTile("cross", 0), cross.Cells()[new Cell(0, 0)]);
    }

    [Fact]
    public void Painting_order_doesnt_change_the_tiles()
    {
        Cell[] cells = [new(0, 0), new(1, 0), new(2, 0), new(1, 1), new(1, -1), new(3, 3)];
        var together = Painted(cells);
        var oneByOne = Square();
        foreach (var cell in cells.Reverse())
        {
            oneByOne.Paint(cell, Walls);
        }

        Assert.Equal(Sorted(together), Sorted(oneByOne));
    }

    [Fact]
    public void Without_an_exact_tile_the_one_with_fewest_sides_wrong_is_used()
    {
        var map = Square([new("straight", ModelRef.Cube, Walls, "1010"), new("cross", ModelRef.Cube, Walls, "1111")]);

        map.Paint([new Cell(0, 0), new Cell(0, 1)], Walls);

        Assert.Equal(new PlacedTile("straight", 1), map.Cells()[new Cell(0, 0)]);
    }

    [Fact]
    public void Erasing_a_wall_fits_its_neighbours_again()
    {
        var map = Painted(new Cell(0, 0), new Cell(1, 0), new Cell(2, 0));

        Assert.True(map.Erase(new Cell(1, 0)));
        Assert.False(map.Erase(new Cell(1, 0)));

        Assert.Equal(new PlacedTile("post"), map.Cells()[new Cell(0, 0)]);
        Assert.Equal(new PlacedTile("post"), map.Cells()[new Cell(2, 0)]);
        Assert.False(map.Cells().Contains(new Cell(1, 0)));
    }

    [Fact]
    public void Other_groups_and_tiles_set_by_hand_dont_join_a_wall()
    {
        var map = Square();
        map.Set(new Cell(1, 0), "statue", turns: 3);
        map.Paint(new Cell(0, 1), "floor");

        map.Paint(new Cell(0, 0), Walls);

        Assert.Equal(new PlacedTile("post"), map.Cells()[new Cell(0, 0)]);
        Assert.Equal(new PlacedTile("statue", 3), map.Cells()[new Cell(1, 0)]);
        Assert.Equal(new PlacedTile("floor"), map.Cells()[new Cell(0, 1)]);
    }

    [Fact]
    public void Tiles_that_fit_equally_are_mixed_the_same_way_every_time()
    {
        Tile[] floors = [new("a", ModelRef.Plane, "floor"), new("b", ModelRef.Plane, "floor"), new("c", ModelRef.Plane, "floor")];
        var cells = Enumerable.Range(0, 100).Select(i => new Cell(i % 10, i / 10)).ToArray();
        var first = Square(floors);
        var second = Square(floors);

        first.Paint(cells, "floor");
        second.Paint(cells, "floor");

        Assert.Equal(Sorted(first), Sorted(second));
        var used = first.Cells().Select(pair => pair.Value.Tile).Distinct().Order();
        Assert.Equal(["a", "b", "c"], used);
        Assert.All(first.Cells(), pair => Assert.Equal(0, pair.Value.Turns));
    }

    [Fact]
    public void A_hex_coast_turns_by_sixths_to_face_its_neighbour()
    {
        // Hex.Sides starts at (1, 0) and each sixth turn moves a side to the next one.
        var map = new TileMap<Hex>(new TileSet<Hex>([new Tile("shore", ModelRef.Cube, "land", "100000")]), new HexGrid(1f));

        map.Paint([new Hex(0, 0), new Hex(0, 1)], "land");

        Assert.Equal(new PlacedTile("shore", 5), map.Cells()[new Hex(0, 0)]);
        Assert.Equal(new PlacedTile("shore", 2), map.Cells()[new Hex(0, 1)]);
    }

    [Fact]
    public void A_turned_tile_points_its_joined_side_at_its_neighbour()
    {
        var square = Painted(new Cell(0, 0), new Cell(1, 0));
        var hex = new TileMap<Hex>(new TileSet<Hex>([new Tile("shore", ModelRef.Cube, "land", "100000")]), new HexGrid(1f));
        hex.Paint([new Hex(0, 0), new Hex(0, 1)], "land");

        // The end at (1, 0) joins on its own +X, which turns to face −X; the shore at (0, 0) faces (0, 1), up and right.
        AssertNear(-Vector3.UnitX, Vector3.TransformNormal(Vector3.UnitX, square.Transform(new Cell(1, 0))));
        AssertNear(new Vector3(0.5f, 0f, MathF.Sqrt(3f) / 2f), Vector3.TransformNormal(Vector3.UnitX, hex.Transform(new Hex(0, 0))));
    }

    [Fact]
    public void A_tile_is_moved_by_its_offset_turned_and_put_on_the_turned_grid()
    {
        var grid = new SquareGrid(2f, new GridPlacement(new Vector3(10f, 1f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f)));
        var map = new TileMap<Cell>(new TileSet<Cell>([new Tile("lamp", ModelRef.Cube, Offset: new Vector3(0.5f, 0.25f, 0f))]), grid);
        map.Set(new Cell(0, 0), "lamp", turns: 1);

        // The offset (0.5, 0.25, 0) turns once to (0, 0.25, −0.5), then the grid's quarter turn takes it to (−0.5, 0.25, 0).
        // The cell's middle (1, 0, 1) on the grid turns to (1, 0, −1), so it lands at (10.5, 1.25, −1).
        AssertNear(new Vector3(10.5f, 1.25f, -1f), Vector3.Transform(Vector3.Zero, map.Transform(new Cell(0, 0))));
    }

    [Fact]
    public void Each_floor_of_a_layered_map_is_its_own()
    {
        var floors = new LayeredGrid<Cell>(new SquareGrid(1f), 0f, 3f);
        var map = new TileMap<Cell>(new TileSet<Cell>(WallTiles), floors);

        map.Paint([new Cell(0, 0), new Cell(1, 0)], Walls, floor: 1);
        map.Paint(new Cell(1, 0), Walls);

        Assert.Equal(2, map.Floors);
        Assert.Equal(new PlacedTile("post"), map.Cells(0)[new Cell(1, 0)]);
        Assert.Equal(new PlacedTile("end", 2), map.Cells(1)[new Cell(1, 0)]);
        Assert.Equal(1.5f, Vector3.Transform(Vector3.Zero, map.Transform(new Cell(1, 0), 1)).X, 4);
        Assert.Equal(3f, Vector3.Transform(Vector3.Zero, map.Transform(new Cell(1, 0), 1)).Y, 4);
    }

    [Fact]
    public void A_level_saves_as_json_and_loads_back_through_content()
    {
        var map = Painted(new Cell(0, 0), new Cell(1, 0), new Cell(0, -1));
        map.Set(new Cell(5, 5), "statue", turns: 2);
        var files = new MemoryContent();
        files["levels/hall.json"] = map.ToLevel("hall").ToJson();

        var level = new Content(files).LoadAll<TileLevel>("levels")["hall"];
        var loaded = Square();
        loaded.Load(level);

        Assert.Equal(Sorted(map), Sorted(loaded));
        Assert.Equal(Sorted(map), Sorted(FromLevel(TileLevel.FromJson(map.ToLevel("hall").ToJson()))));
    }

    [Fact]
    public void A_level_writes_cells_row_by_row_and_leaves_out_defaults()
    {
        var map = Square();
        map.Set(new Cell(1, 2), "statue", turns: 1);
        map.Set(new Cell(0, 0), "statue");

        string json = map.ToLevel("hall").ToJson().ReplaceLineEndings("\n");

        Assert.Equal(
            "{\n  \"id\": \"hall\",\n  \"cells\": [\n    {\n      \"at\": [\n        0,\n        0\n      ],\n      \"tile\": \"statue\"\n    },\n" +
            "    {\n      \"at\": [\n        1,\n        2\n      ],\n      \"tile\": \"statue\",\n      \"turns\": 1\n    }\n  ]\n}",
            json);
    }

    [Fact]
    public void A_level_that_paints_fits_its_cells_whatever_their_order()
    {
        var level = new TileLevel("hall", [
            new TileLevelCell([1, 1], Paint: Walls),
            new TileLevelCell([0, 0], Paint: Walls),
            new TileLevelCell([0, 1], Paint: Walls),
            new TileLevelCell([1, 0], Paint: Walls),
            new TileLevelCell([4, 4], "statue", 1),
        ]);

        var map = FromLevel(level);

        Assert.Equal(new PlacedTile("corner", 0), map.Cells()[new Cell(0, 0)]);
        Assert.Equal(new PlacedTile("corner", 2), map.Cells()[new Cell(1, 1)]);
        Assert.Equal(new PlacedTile("statue", 1), map.Cells()[new Cell(4, 4)]);
        Assert.Equal(5, map.Cells().Count);
    }

    [Fact]
    public void Loading_a_level_empties_the_map_first()
    {
        var map = Painted(new Cell(9, 9));

        map.Load(new TileLevel("hall", [new TileLevelCell([0, 0], "statue")]));

        Assert.False(map.Cells().Contains(new Cell(9, 9)));
        Assert.Equal(1, map.Cells().Count);
    }

    [Theory]
    [InlineData("[1]", "\"statue\"", "null", 0, "Cell 0 of the level hall needs \"at\" as two whole numbers, such as [3, -2].")]
    [InlineData("[0, 0]", "\"statue\"", "\"walls\"", 0, "Cell 0 of the level hall needs either a tile or a group to paint, but not both.")]
    [InlineData("[0, 0]", "null", "null", 0, "Cell 0 of the level hall needs either a tile or a group to paint, but not both.")]
    [InlineData("[0, 0]", "\"statue\"", "null", 2, "Cell 0 of the level hall is on floor 2, but the map has 1.")]
    [InlineData("[0, 0]", "null", "\"water\"", 0, "Cell 0 of the level hall paints water, but no tile is in that group.")]
    [InlineData("[0, 0]", "\"tree\"", "null", 0, "Cell 0 of the level hall names the tile tree, but the tile set has none called that.")]
    public void A_malformed_level_cell_is_refused_saying_which(string at, string tile, string paint, int floor, string message)
    {
        var level = TileLevel.FromJson($"{{ \"id\": \"hall\", \"cells\": [{{ \"at\": {at}, \"tile\": {tile}, \"paint\": {paint}, \"floor\": {floor} }}] }}");

        var problem = Assert.Throws<ArgumentException>(() => Square().Load(level));

        Assert.StartsWith(message, problem.Message);
    }

    [Fact]
    public void Tiles_load_from_content_and_name_their_model()
    {
        var files = new MemoryContent();
        files.AddModel("tiles/wall.glb");
        files["tiles/straight.json"] = "{ \"id\": \"straight\", \"model\": \"tiles/wall.glb\", \"group\": \"walls\", \"connects\": \"1010\", \"offset\": [0, 0.5, 0] }";
        var content = new Content(files);

        var tiles = new TileSet<Cell>(content.LoadAll<Tile>("tiles"));

        Assert.Equal(content.Model("tiles/wall.glb"), tiles["straight"].Model);
        Assert.Equal(new Vector3(0f, 0.5f, 0f), tiles["straight"].Offset);
        Assert.Equal("1010", tiles["straight"].Connects);
    }

    [Theory]
    [InlineData("101", "The tile bad connects \"101\", but a cell has 4 sides, so it needs 4 of 1 or 0, one for each side in Sides order, such as \"1100\".")]
    [InlineData("10x0", "The tile bad connects \"10x0\", but a cell has 4 sides, so it needs 4 of 1 or 0, one for each side in Sides order, such as \"1100\".")]
    public void A_tile_with_malformed_connects_is_refused(string connects, string message)
    {
        var problem = Assert.Throws<ArgumentException>(() => new TileSet<Cell>([new Tile("bad", ModelRef.Cube, Walls, connects)]));

        Assert.Equal(message, problem.Message);
    }

    [Fact]
    public void Mistakes_in_a_tile_set_or_a_map_say_what_is_wrong()
    {
        var noGroup = Assert.Throws<ArgumentException>(() => new TileSet<Cell>([new Tile("bad", ModelRef.Cube, Connects: "1000")]));
        var twice = Assert.Throws<ArgumentException>(() => new TileSet<Cell>([new Tile("a", ModelRef.Cube), new Tile("a", ModelRef.Cube)]));
        var unknownTile = Assert.Throws<ArgumentException>(() => Square().Set(default, "tree"));
        var unknownGroup = Assert.Throws<ArgumentException>(() => Square().Paint(new Cell(0, 0), "water"));
        var noFloor = Assert.Throws<ArgumentOutOfRangeException>(() => Square().Cells(1));

        Assert.Equal("The tile bad says which sides it connects, but it has no group, so painting never picks it. Give it a group.", noGroup.Message);
        Assert.StartsWith("The tile a is in the tile set twice.", twice.Message);
        Assert.StartsWith("There is no tile called tree in the map's tile set.", unknownTile.Message);
        Assert.StartsWith("No tile in the map's tile set is in the group water.", unknownGroup.Message);
        Assert.StartsWith("The map has 1 floors, 0 to 0, so there is no floor 1.", noFloor.Message);
    }

    [Fact]
    public void Drawing_a_map_draws_each_tile_where_it_goes()
    {
        var map = Painted(new Cell(0, 0), new Cell(1, 0));
        map.Set(new Cell(20, 0), "statue");
        var view = new View();

        view.Begin(0f);
        view.TileMap(1, map);

        var drawn = view.Drawn.ToArray();
        Assert.Equal(3, drawn.Length);
        var statue = Assert.Single(drawn, draw => draw.Model == ModelRef.Sphere);
        Assert.Equal(map.Transform(new Cell(20, 0)), statue.Transform);
        Assert.Contains(drawn, draw => draw.Transform == map.Transform(new Cell(1, 0)));
    }

    [Fact]
    public void A_drawn_map_follows_changes_to_its_cells_and_its_grid()
    {
        var map = Painted(new Cell(0, 0));
        var view = new View();
        view.Begin(0f);
        view.TileMap(1, map);

        map.Paint(new Cell(1, 0), Walls);
        view.Begin(0f);
        view.TileMap(1, map);
        Assert.Equal(2, view.Drawn.Length);
        Assert.Contains(view.Drawn.ToArray(), draw => draw.Transform == map.Transform(new Cell(1, 0)));

        map.Grid().Placement = new GridPlacement(new Vector3(0f, 5f, 0f));
        view.Begin(0f);
        view.TileMap(1, map);
        Assert.All(view.Drawn.ToArray(), draw => Assert.Equal(5f, draw.Transform.Translation.Y, 4));
    }

    [Fact]
    public void Drawing_a_map_that_isnt_changing_allocates_nothing()
    {
        var map = Painted([.. Enumerable.Range(0, 40).Select(i => new Cell(i, 0))]);
        var view = new View();
        void Frame()
        {
            view.Begin(0f);
            view.TileMap(1, map);
        }

        Frame();
        Frame();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Frame();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(40, view.Drawn.Length);
    }

    private static TileMap<Cell> FromLevel(TileLevel level)
    {
        var map = Square();
        map.Load(level);
        return map;
    }

    private static (int, int, string, int)[] Sorted<TCell>(TileMap<TCell> map)
        where TCell : struct, IGridCell<TCell> =>
        [.. map.Cells().Select(pair => (pair.Key.Pair.A, pair.Key.Pair.B, pair.Value.Tile, pair.Value.Turns)).Order()];

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-4f, $"Expected {expected} but got {actual}.");
    }
}
