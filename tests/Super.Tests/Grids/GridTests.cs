using System.Globalization;
using System.Numerics;
using Super.Testing;

namespace Super.Tests.Grids;

public sealed class GridTests
{
    private static readonly float Root3 = MathF.Sqrt(3f);

    [Fact]
    public void A_square_grid_finds_the_cell_under_a_point_and_its_middle()
    {
        var grid = new SquareGrid(new Vector2(2f, 1f), new GridPlacement(new Vector3(10f, 5f, 0f)));

        var cell = grid.CellAt(new Vector3(14.5f, 9f, -0.5f));

        Assert.Equal(new Cell(2, -1), cell);
        AssertNear(new Vector3(15f, 5f, -0.5f), grid.Center(cell));
    }

    [Fact]
    public void A_square_cell_has_its_four_corners_round_from_the_origin_side()
    {
        var grid = new SquareGrid(2f);
        Span<Vector3> corners = stackalloc Vector3[4];

        grid.Corners(new Cell(1, 0), corners);

        AssertNear(new Vector3(2f, 0f, 0f), corners[0]);
        AssertNear(new Vector3(4f, 0f, 0f), corners[1]);
        AssertNear(new Vector3(4f, 0f, 2f), corners[2]);
        AssertNear(new Vector3(2f, 0f, 2f), corners[3]);
    }

    [Fact]
    public void A_pointy_hex_grid_puts_the_next_hexes_along_x_and_towards_z()
    {
        var grid = new HexGrid(1f);

        AssertNear(new Vector3(Root3, 0f, 0f), grid.Center(new Hex(1, 0)));
        AssertNear(new Vector3(Root3 / 2f, 0f, 1.5f), grid.Center(new Hex(0, 1)));
    }

    [Fact]
    public void A_flat_hex_grid_puts_the_next_hexes_along_its_columns()
    {
        var grid = new HexGrid(2f, HexTop.Flat);

        AssertNear(new Vector3(3f, 0f, Root3), grid.Center(new Hex(1, 0)));
        AssertNear(new Vector3(0f, 0f, 2f * Root3), grid.Center(new Hex(0, 1)));
    }

    [Fact]
    public void A_point_belongs_to_the_hex_on_its_side_of_the_edge_between_two()
    {
        // The edge between pointy hexes 0, 0 and 1, 0 of size 1 is half of root 3, about 0.866, along X.
        var grid = new HexGrid(1f);

        Assert.Equal(new Hex(0, 0), grid.HexAt(new Vector3(0.8f, 0f, 0f)));
        Assert.Equal(new Hex(1, 0), grid.HexAt(new Vector3(0.9f, 0f, 0f)));
    }

    [Theory]
    [InlineData(HexTop.Pointy)]
    [InlineData(HexTop.Flat)]
    public void A_hexs_corners_are_its_size_from_its_middle_and_inside_it(HexTop top)
    {
        var grid = new HexGrid(1.5f, top);
        var hex = new Hex(2, -3);
        Span<Vector3> corners = stackalloc Vector3[6];

        grid.Corners(hex, corners);

        var middle = grid.Center(hex);
        foreach (var corner in corners)
        {
            Assert.Equal(1.5f, Vector3.Distance(middle, corner), 1e-4f);
            Assert.Equal(hex, grid.HexAt(Vector3.Lerp(middle, corner, 0.98f)));
        }
    }

    [Fact]
    public void A_ray_picks_the_cell_on_a_raised_turned_grid()
    {
        // Turned a quarter about +Y, the grid's X runs along the world's -Z and its Z along the world's +X.
        var placement = new GridPlacement(new Vector3(0f, 3f, 0f), Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f));
        var grid = new SquareGrid(1f, placement);

        var cell = grid.CellAt(new Ray(new Vector3(0.5f, 10f, -2.5f), -Vector3.UnitY));

        Assert.Equal(new Cell(2, 0), cell);
    }

    [Fact]
    public void A_ray_picks_the_hex_on_a_grid_stood_up_like_a_wall()
    {
        // Turned a quarter about +X, the grid's Z runs up the world's Y and its top faces -Z.
        var placement = new GridPlacement(new Vector3(0f, 0f, 4f), Quaternion.CreateFromAxisAngle(Vector3.UnitX, -MathF.PI / 2f));
        var grid = new HexGrid(1f, HexTop.Pointy, placement);

        var hex = grid.HexAt(new Ray(new Vector3(Root3 / 2f, 1.5f, 0f), Vector3.UnitZ));

        Assert.Equal(new Hex(0, 1), hex);
    }

    [Fact]
    public void A_ray_alongside_the_grid_or_pointing_away_picks_nothing()
    {
        var grid = new SquareGrid(1f);

        Assert.Null(grid.CellAt(new Ray(new Vector3(0f, 1f, 0f), Vector3.UnitX)));
        Assert.Null(grid.CellAt(new Ray(new Vector3(0f, 1f, 0f), Vector3.UnitY)));
    }

    [Theory, Seeds(15)]
    public void A_cell_found_from_a_point_has_its_middle_in_the_same_cell(int seed)
    {
        var random = new Random(seed);
        var placement = RandomPlacement(random);
        var squares = new SquareGrid(new Vector2(0.5f + random.NextSingle() * 3f, 0.5f + random.NextSingle() * 3f), placement);
        var hexes = new HexGrid(0.5f + random.NextSingle() * 3f, random.Next(2) == 0 ? HexTop.Pointy : HexTop.Flat, placement);
        var point = placement.Origin + new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()) * 40f - new Vector3(20f);

        var cell = squares.CellAt(point);
        var hex = hexes.HexAt(point);

        Assert.Equal(cell, squares.CellAt(squares.Center(cell)));
        Assert.Equal(hex, hexes.HexAt(hexes.Center(hex)));
    }

    [Theory, Seeds(15)]
    public void A_ray_down_onto_a_grid_picks_the_cell_of_the_point_it_hits(int seed)
    {
        var random = new Random(seed);
        var grid = new HexGrid(1f + random.NextSingle(), HexTop.Flat, RandomPlacement(random));
        var hex = new Hex(random.Next(-9, 10), random.Next(-9, 10));
        var middle = grid.Center(hex);
        var up = grid.Placement.Up;

        var picked = grid.HexAt(new Ray(middle + up * 5f, -up));

        Assert.Equal(hex, picked);
    }

    [Fact]
    public void Neighbours_are_one_step_away_and_see_each_other()
    {
        var cell = new Cell(3, -2);
        foreach (var side in Cell.Sides)
        {
            Assert.Equal(1, Cell.Manhattan(cell, cell + side));
            Assert.Contains(new Cell(-side.X, -side.Z), Cell.Sides.ToArray());
        }

        foreach (var step in Cell.Around)
        {
            Assert.Equal(1, Cell.Chebyshev(cell, cell + step));
            Assert.Contains(new Cell(-step.X, -step.Z), Cell.Around.ToArray());
        }

        var hex = new Hex(-1, 4);
        foreach (var side in Hex.Sides)
        {
            Assert.Equal(1, Hex.Distance(hex, hex + side));
            Assert.Contains(new Hex(-side.Q, -side.R), Hex.Sides.ToArray());
        }

        Assert.Equal(8, Cell.Around.ToArray().Distinct().Count());
        Assert.Equal(6, Hex.Sides.ToArray().Distinct().Count());
    }

    [Fact]
    public void Distances_count_the_steps_each_way_of_moving()
    {
        Assert.Equal(7, Cell.Manhattan(new Cell(1, 1), new Cell(4, -3)));
        Assert.Equal(4, Cell.Chebyshev(new Cell(1, 1), new Cell(4, -3)));
        Assert.Equal(3, Hex.Distance(new Hex(0, 0), new Hex(3, -1)));
        Assert.Equal(5, Hex.Distance(new Hex(2, 1), new Hex(-1, -1)));
    }

    [Fact]
    public void A_line_steps_from_one_end_to_the_other()
    {
        Assert.Equal([new(0, 0), new(1, 1), new(2, 1), new(3, 2), new(4, 2)], Cell.Line(new Cell(0, 0), new Cell(4, 2)));
        Assert.Equal([new(0, 0), new(1, 0), new(2, 0), new(3, 0)], Hex.Line(new Hex(0, 0), new Hex(3, 0)));
        Assert.Equal([new Hex(5, 5)], Hex.Line(new Hex(5, 5), new Hex(5, 5)));
    }

    [Theory, Seeds(15)]
    public void A_line_has_one_more_cell_than_the_distance_and_each_step_is_a_neighbour(int seed)
    {
        var random = new Random(seed);
        var (fromCell, toCell) = (new Cell(random.Next(-20, 21), random.Next(-20, 21)), new Cell(random.Next(-20, 21), random.Next(-20, 21)));
        var (fromHex, toHex) = (new Hex(random.Next(-20, 21), random.Next(-20, 21)), new Hex(random.Next(-20, 21), random.Next(-20, 21)));

        var cells = Cell.Line(fromCell, toCell).ToList();
        var hexes = Hex.Line(fromHex, toHex).ToList();

        Assert.Equal(Cell.Chebyshev(fromCell, toCell) + 1, cells.Count);
        Assert.Equal((fromCell, toCell), (cells[0], cells[^1]));
        Assert.All(cells.Zip(cells.Skip(1)), pair => Assert.Equal(1, Cell.Chebyshev(pair.First, pair.Second)));
        Assert.Equal(Hex.Distance(fromHex, toHex) + 1, hexes.Count);
        Assert.Equal((fromHex, toHex), (hexes[0], hexes[^1]));
        Assert.All(hexes.Zip(hexes.Skip(1)), pair => Assert.Equal(1, Hex.Distance(pair.First, pair.Second)));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 7, 6)]
    [InlineData(2, 19, 12)]
    [InlineData(3, 37, 18)]
    public void A_hex_range_and_ring_hold_every_hex_that_far(int steps, int inRange, int onRing)
    {
        var center = new Hex(2, -5);

        var range = Hex.Range(center, steps).ToList();
        var ring = Hex.Ring(center, steps).ToList();
        var spiral = Hex.Spiral(center, steps).ToList();

        Assert.Equal(inRange, range.Distinct().Count());
        Assert.All(range, hex => Assert.True(Hex.Distance(center, hex) <= steps));
        Assert.Equal(onRing, ring.Distinct().Count());
        Assert.All(ring, hex => Assert.Equal(steps, Hex.Distance(center, hex)));
        Assert.Equal(range.Order(HexOrder.Instance), spiral.Order(HexOrder.Instance));
        Assert.All(ring.Zip(ring.Skip(1)), pair => Assert.Equal(1, Hex.Distance(pair.First, pair.Second)));
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(1, 9, 8)]
    [InlineData(2, 25, 16)]
    public void A_square_range_and_ring_hold_every_cell_that_far(int steps, int inRange, int onRing)
    {
        var center = new Cell(-4, 7);

        var range = Cell.Range(center, steps).ToList();
        var ring = Cell.Ring(center, steps).ToList();
        var spiral = Cell.Spiral(center, steps).ToList();

        Assert.Equal(inRange, range.Distinct().Count());
        Assert.All(range, cell => Assert.True(Cell.Chebyshev(center, cell) <= steps));
        Assert.Equal(onRing, ring.Distinct().Count());
        Assert.All(ring, cell => Assert.Equal(steps, Cell.Chebyshev(center, cell)));
        Assert.Equal(range.Order(CellOrder.Instance), spiral.Order(CellOrder.Instance));
        Assert.All(ring.Zip(ring.Skip(1)), pair => Assert.Equal(1, Cell.Manhattan(pair.First, pair.Second)));
    }

    [Fact]
    public void A_ring_goes_round_from_the_side_it_starts_on()
    {
        Assert.Equal(
            [new(1, -1), new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0), new(-1, -1), new(0, -1)],
            Cell.Ring(new Cell(0, 0), 1));
        Assert.Equal(
            [new(-1, 1), new(0, 1), new(1, 0), new(1, -1), new(0, -1), new(-1, 0)],
            Hex.Ring(new Hex(0, 0), 1));
    }

    [Fact]
    public void One_turn_moves_a_cell_the_way_a_positive_turn_about_up_does()
    {
        Assert.Equal(new Cell(0, -1), Cell.Rotate(new Cell(1, 0), new Cell(0, 0), 1));
        Assert.Equal(new Cell(5, 4), Cell.Rotate(new Cell(6, 5), new Cell(5, 5), 1));
        Assert.Equal(new Cell(0, 1), Cell.Rotate(new Cell(1, 0), new Cell(0, 0), -1));
        Assert.Equal(new Hex(1, -1), Hex.Rotate(new Hex(1, 0), new Hex(0, 0), 1));
        Assert.Equal(new Hex(0, 1), Hex.Rotate(new Hex(1, 0), new Hex(0, 0), -1));
    }

    [Theory, Seeds(15)]
    public void Turning_a_cell_matches_turning_its_middle_and_a_full_turn_is_none(int seed)
    {
        var random = new Random(seed);
        var cell = new Cell(random.Next(-9, 10), random.Next(-9, 10));
        var cellAround = new Cell(random.Next(-9, 10), random.Next(-9, 10));
        var hex = new Hex(random.Next(-9, 10), random.Next(-9, 10));
        var hexAround = new Hex(random.Next(-9, 10), random.Next(-9, 10));
        var squares = new SquareGrid(1.5f);
        var hexes = new HexGrid(1.5f, random.Next(2) == 0 ? HexTop.Pointy : HexTop.Flat);
        int turns = random.Next(-7, 8);

        var quarter = Quaternion.CreateFromAxisAngle(Vector3.UnitY, turns * MathF.PI / 2f);
        var sixth = Quaternion.CreateFromAxisAngle(Vector3.UnitY, turns * MathF.PI / 3f);
        var cellPivot = squares.Center(cellAround);
        var hexPivot = hexes.Center(hexAround);

        AssertNear(cellPivot + Vector3.Transform(squares.Center(cell) - cellPivot, quarter), squares.Center(Cell.Rotate(cell, cellAround, turns)));
        AssertNear(hexPivot + Vector3.Transform(hexes.Center(hex) - hexPivot, sixth), hexes.Center(Hex.Rotate(hex, hexAround, turns)));
        Assert.Equal(cell, Cell.Rotate(cell, cellAround, 4));
        Assert.Equal(hex, Hex.Rotate(hex, hexAround, 6));
    }

    [Fact]
    public void A_placement_left_at_its_default_lies_flat_at_the_origin()
    {
        var placement = new GridPlacement();

        Assert.Equal(new Vector2(3f, -2f), placement.OnGrid(new Vector3(3f, 7f, -2f)));
        AssertNear(Vector3.UnitY, placement.Up);
    }

    [Fact]
    public void A_grid_refuses_cells_without_a_size()
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("sv-SE");
        ArgumentOutOfRangeException square, hex, top;
        try
        {
            square = Assert.Throws<ArgumentOutOfRangeException>(() => new SquareGrid(new Vector2(0.5f, -1f)));
            hex = Assert.Throws<ArgumentOutOfRangeException>(() => new HexGrid(float.NaN));
            top = Assert.Throws<ArgumentOutOfRangeException>(() => new HexGrid(1f, (HexTop)7));
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        Assert.StartsWith("A square grid's cells must be more than 0 metres on each side, but they are 0.5 by -1.", square.Message);
        Assert.StartsWith("A hex grid's hexagons must be more than 0 metres from the middle to a corner, but they are NaN.", hex.Message);
        Assert.StartsWith("A hexagon's top is pointy or flat, but it is 7.", top.Message);
    }

    [Fact]
    public void Corners_need_room_for_every_corner()
    {
        var square = Assert.Throws<ArgumentException>(() => new SquareGrid(1f).Corners(new Cell(0, 0), new Vector3[3]));
        var hex = Assert.Throws<ArgumentException>(() => new HexGrid(1f).Corners(new Hex(0, 0), new Vector3[5]));

        Assert.StartsWith("A square cell has 4 corners, but there is room for 3.", square.Message);
        Assert.StartsWith("A hexagon has 6 corners, but there is room for 5.", hex.Message);
    }

    private static GridPlacement RandomPlacement(Random random)
    {
        var axis = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f) + new Vector3(0f, 0.01f, 0f));
        var origin = new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()) * 100f - new Vector3(50f);
        return new GridPlacement(origin, Quaternion.CreateFromAxisAngle(axis, random.NextSingle() * MathF.Tau));
    }

    private static void AssertNear(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 1e-3f, $"Expected {expected} but got {actual}.");
    }

    private sealed class HexOrder : IComparer<Hex>
    {
        public static readonly HexOrder Instance = new();

        public int Compare(Hex a, Hex b) => (a.Q, a.R).CompareTo((b.Q, b.R));
    }

    private sealed class CellOrder : IComparer<Cell>
    {
        public static readonly CellOrder Instance = new();

        public int Compare(Cell a, Cell b) => (a.X, a.Z).CompareTo((b.X, b.Z));
    }
}
