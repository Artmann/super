using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;
using Rendition.Passes;

namespace Super.Tests.Grids;

public sealed class GridOverlayTests
{
    private static readonly GridLines White = new(new Color(1f, 1f, 1f, 1f));
    private static readonly CellFill Blue = new(new Color(0.2f, 0.4f, 1f, 0.3f));

    private static ViewOverlays Overlays() => new View().Overlays;

    [Fact]
    public void A_square_grid_one_step_out_covers_three_by_three_cells_round_the_middle()
    {
        var overlays = Overlays();
        overlays.Grid(new SquareGrid(2f), new Cell(0, 0), 1, White);

        var drawn = Assert.Single(overlays.Drawn.ToArray());
        Assert.Equal(GridShape.Square, drawn.Shape);
        Assert.Equal(new Vector2(2f, 2f), drawn.CellSize);
        Assert.Equal(new GridCellWindow((-1, -1), 3, 3, 0), drawn.Cells);
        Assert.Equal([0x1FFu], overlays.Words.ToArray());
    }

    [Fact]
    public void A_hex_grid_one_step_out_leaves_out_the_two_corners_of_its_window_two_steps_away()
    {
        var overlays = Overlays();
        overlays.Grid(new HexGrid(1f), new Hex(0, 0), 1, White);

        // Of the window from (-1, -1) to (1, 1), (-1, -1) and (1, 1) are two steps out: bits 0 and 8.
        var drawn = Assert.Single(overlays.Drawn.ToArray());
        Assert.Equal(GridShape.PointyHex, drawn.Shape);
        Assert.Equal(new GridCellWindow((-1, -1), 3, 3, 0), drawn.Cells);
        Assert.Equal([0xFEu], overlays.Words.ToArray());
    }

    [Fact]
    public void A_flat_hex_grid_draws_as_flat_hexes_of_its_size()
    {
        var overlays = Overlays();
        overlays.Cell(new HexGrid(0.8f, HexTop.Flat), new Hex(0, 0), Blue);

        var drawn = Assert.Single(overlays.Drawn.ToArray());
        Assert.Equal(GridShape.FlatHex, drawn.Shape);
        Assert.Equal(new Vector2(0.8f, 0.8f), drawn.CellSize);
    }

    [Fact]
    public void Filled_cells_get_the_smallest_window_round_them_and_one_bit_each()
    {
        var overlays = Overlays();
        overlays.Cells(new SquareGrid(1f), [new Cell(2, 5), new Cell(4, 6)], Blue);

        // From (2, 5), three columns and two rows: (2, 5) is bit 0 and (4, 6) is bit 2 + 1 * 3.
        var drawn = Assert.Single(overlays.Drawn.ToArray());
        Assert.Equal(new GridCellWindow((2, 5), 3, 2, 0), drawn.Cells);
        Assert.Equal([0x21u], overlays.Words.ToArray());
    }

    [Fact]
    public void A_second_overlay_starts_its_bits_after_the_first_ones()
    {
        var overlays = Overlays();
        var grid = new SquareGrid(1f);
        overlays.Cell(grid, new Cell(0, 0), Blue);
        overlays.Cells(grid, (IEnumerable<Cell>)[new Cell(3, 3), new Cell(4, 3)], Blue);

        var drawn = overlays.Drawn.ToArray();
        Assert.Equal(2, drawn.Length);
        Assert.Equal(0, drawn[0].Cells.Start);
        Assert.Equal(new GridCellWindow((3, 3), 2, 1, 1), drawn[1].Cells);
        Assert.Equal([0x1u, 0x3u], overlays.Words.ToArray());
    }

    [Fact]
    public void No_cells_draw_no_overlay()
    {
        var overlays = Overlays();
        overlays.Cells(new SquareGrid(1f), ReadOnlySpan<Cell>.Empty, Blue);

        Assert.Equal(0, overlays.Count);
        Assert.True(overlays.Words.IsEmpty);
    }

    [Fact]
    public void A_focus_lands_on_the_grid_and_no_focus_draws_lines_all_over()
    {
        var overlays = Overlays();
        var grid = new SquareGrid(1f, new GridPlacement(new Vector3(10f, 0f, 5f)));
        overlays.Grid(grid, new Cell(0, 0), 1, White with { Focus = new Vector3(12f, 1f, 8f), FocusRadius = 6f });
        overlays.Grid(grid, new Cell(0, 0), 1, White with { FocusRadius = 6f });

        var drawn = overlays.Drawn.ToArray();
        Assert.Equal(new Vector2(2f, 3f), drawn[0].Focus);
        Assert.Equal(6f, drawn[0].FocusRadius);
        Assert.Equal(0f, drawn[1].FocusRadius);
    }

    [Fact]
    public void A_fill_borders_in_its_own_colour_made_solid_unless_told_otherwise()
    {
        var overlays = Overlays();
        var grid = new SquareGrid(1f);
        overlays.Cell(grid, new Cell(0, 0), Blue);
        overlays.Cell(grid, new Cell(0, 0), Blue with { BorderColor = new Color(1f, 0f, 0f, 0.5f) });

        var drawn = overlays.Drawn.ToArray();
        Assert.Equal(new Vector4(0.2f, 0.4f, 1f, 0.3f), drawn[0].FillColor);
        Assert.Equal(new Vector4(0.2f, 0.4f, 1f, 1f), drawn[0].BorderColor);
        Assert.Equal(new Vector4(1f, 0f, 0f, 0.5f), drawn[1].BorderColor);
    }

    [Fact]
    public void The_overlay_turns_and_moves_with_the_grid()
    {
        var overlays = Overlays();
        var turn = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2f);
        overlays.Cell(new SquareGrid(1f, new GridPlacement(new Vector3(1f, 2f, 3f), turn)), new Cell(0, 0), Blue);

        // A quarter turn about up takes the grid's X to the world's -Z.
        var drawn = Assert.Single(overlays.Drawn.ToArray());
        var x = Vector3.Transform(Vector3.UnitX, drawn.Grid);
        Assert.Equal(1f, x.X, 1e-5f);
        Assert.Equal(2f, x.Y, 1e-5f);
        Assert.Equal(2f, x.Z, 1e-5f);
    }

    [Fact]
    public void Beginning_a_frame_clears_the_overlays()
    {
        var view = new View();
        view.Overlays.Cell(new SquareGrid(1f), new Cell(0, 0), Blue);
        view.Begin(0f);

        Assert.Equal(0, view.Overlays.Count);
        Assert.True(view.Overlays.Words.IsEmpty);
    }

    [Fact]
    public void Drawing_overlays_each_frame_allocates_nothing_once_warm()
    {
        var view = new View();
        var grid = new SquareGrid(1f);
        Cell[] path = [new Cell(0, 0), new Cell(1, 0), new Cell(1, 1)];
        void Frame()
        {
            view.Begin(0f);
            view.Overlays.Grid(grid, new Cell(0, 0), 3, White with { Focus = Vector3.Zero });
            view.Overlays.Cells(grid, path.AsSpan(), Blue);
            view.Overlays.Cell(grid, new Cell(2, 2), Blue);
        }

        Frame();
        long before = GC.GetAllocatedBytesForCurrentThread();
        Frame();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Another_kind_of_grid_is_refused()
    {
        var overlays = Overlays();
        var error = Assert.Throws<NotSupportedException>(() => overlays.Cell(new OtherGrid(), new Cell(0, 0), Blue));
        Assert.Equal("Grid overlays draw on a SquareGrid or a HexGrid, not a OtherGrid.", error.Message);
    }

    [Fact]
    public void Negative_steps_are_refused()
    {
        var overlays = Overlays();
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => overlays.Grid(new SquareGrid(1f), new Cell(0, 0), -1, White));
        Assert.Equal("steps", error.ParamName);
    }

    [Fact]
    public void The_sixty_fifth_overlay_is_refused()
    {
        var overlays = Overlays();
        var grid = new SquareGrid(1f);
        for (int i = 0; i < 64; i++)
        {
            overlays.Cell(grid, new Cell(i, 0), Blue);
        }

        var error = Assert.Throws<InvalidOperationException>(() => overlays.Cell(grid, new Cell(0, 0), Blue));
        Assert.Equal("A frame can draw at most 64 grid overlays. Put cells that look the same in one.", error.Message);
        Assert.Equal(64, overlays.Count);
    }

    [Fact]
    public void Cells_too_far_apart_are_refused()
    {
        var overlays = Overlays();
        var error = Assert.Throws<ArgumentException>(
            () => overlays.Cells(new SquareGrid(1f), [new Cell(0, 0), new Cell(3_000_000, 0)], Blue));
        Assert.StartsWith(
            "A frame's grid overlays can cover at most 2097152 cells between them, counting the gaps between each one's farthest cells, but these cells reach 3000001 by 1.",
            error.Message);
        Assert.Equal("cells", error.ParamName);
    }

    [Fact]
    public void A_negative_line_width_is_refused()
    {
        var overlays = Overlays();
        var error = Assert.Throws<ArgumentException>(
            () => overlays.Grid(new SquareGrid(1f), new Cell(0, 0), 1, White with { Width = -1f }));
        Assert.StartsWith("A grid overlay's line width must be 0 or more, but it is -1.", error.Message);
    }

    [Fact]
    public void A_fill_with_no_depth_is_refused()
    {
        var overlays = Overlays();
        var error = Assert.Throws<ArgumentException>(() => overlays.Cell(new SquareGrid(1f), new Cell(0, 0), Blue with { Depth = 0f }));
        Assert.StartsWith("A grid overlay's depth must be more than 0, but it is 0.", error.Message);
    }

    [Fact]
    public void A_square_overlay_covers_its_cells_and_a_margin_for_the_lines()
    {
        var overlay = new GridOverlayDraw
        {
            Shape = GridShape.Square,
            CellSize = new Vector2(2f, 1f),
            Cells = new GridCellWindow((1, 2), 2, 3, 0),
            LineWidth = 0.1f,
        };

        // Cells 1 to 2 along X at 2 m and 2 to 4 along Z at 1 m cover 2..6 by 2..5, with 0.1 + 2 / 4 round them.
        var (low, high) = GridOverlayBatch.Area(overlay);
        AssertNear(new Vector2(1.4f, 1.4f), low);
        AssertNear(new Vector2(6.6f, 5.6f), high);
    }

    [Fact]
    public void A_hex_overlay_covers_a_cell_size_round_its_middles_and_a_margin()
    {
        var overlay = new GridOverlayDraw
        {
            Shape = GridShape.PointyHex,
            CellSize = new Vector2(1f),
            Cells = new GridCellWindow((0, 0), 1, 1, 0),
        };

        var (low, high) = GridOverlayBatch.Area(overlay);
        AssertNear(new Vector2(-1.25f), low);
        AssertNear(new Vector2(1.25f), high);
    }

    [Fact]
    public void Packing_puts_the_box_round_the_cells_and_the_way_back_out_of_it()
    {
        GridOverlayDraw[] overlays =
        [
            new()
            {
                Grid = Matrix4x4.Identity,
                Shape = GridShape.Square,
                CellSize = new Vector2(1f),
                Cells = new GridCellWindow((0, 0), 1, 1, 0),
                Depth = 1f,
            },
        ];
        var packed = new GpuGridOverlay[1];
        GridOverlayBatch.Pack(overlays, 1, Matrix4x4.Identity, packed);

        // The cell from 0 to 1 with a quarter cell round it is -0.25 to 1.25, a metre up and down.
        var corner = Vector4.Transform(new Vector4(0.5f, 0.5f, 0.5f, 1f), packed[0].ToClip);
        AssertNear(new Vector4(1.25f, 1f, 1.25f, 1f), corner);
        var back = Vector4.Transform(corner, packed[0].FromClip);
        AssertNear(new Vector4(0.5f, 0.5f, 0.5f, 1f), back);
        Assert.Equal(new Vector4(-0.25f, -0.25f, 1.5f, 1.5f), packed[0].Area);
        Assert.Equal((int)GridShape.Square, packed[0].Shape);
    }

    [Fact]
    public void Packing_refuses_too_many_overlays_or_cell_words()
    {
        var many = new GridOverlayDraw[65];
        var into = new GpuGridOverlay[65];
        var tooMany = Assert.Throws<ArgumentException>(() => GridOverlayBatch.Pack(many, 0, Matrix4x4.Identity, into));
        Assert.StartsWith("A frame can draw at most 64 grid overlays, but it was given 65.", tooMany.Message);

        var tooBig = Assert.Throws<ArgumentException>(
            () => GridOverlayBatch.Pack([], GridOverlayBatch.MaxCellWords + 1, Matrix4x4.Identity, into));
        Assert.StartsWith("A frame's grid overlays can cover at most 2097152 cells between them, but they were given 2097184.", tooBig.Message);
    }

    [Fact]
    public void Checking_refuses_cells_past_the_frames_words()
    {
        var overlay = new GridOverlayDraw
        {
            Grid = Matrix4x4.Identity,
            CellSize = new Vector2(1f),
            Cells = new GridCellWindow((0, 0), 8, 8, 1),
            Depth = 1f,
        };

        // 64 cells take two words from word 1, so they need three.
        GridOverlayBatch.Check(overlay, 3);
        var error = Assert.Throws<ArgumentException>(() => GridOverlayBatch.Check(overlay, 2));
        Assert.StartsWith("The grid overlay's cells, 8 by 8 from word 1, run past the frame's 2 words of cells.", error.Message);
    }

    [Fact]
    public void A_packed_overlay_is_as_big_as_the_shader_reads()
    {
        // Two matrices, seven vectors and eight whole numbers, as grid_overlay.glsl lays them out.
        Assert.Equal((2 * 64) + (7 * 16) + (8 * 4), Marshal.SizeOf<GpuGridOverlay>());
    }

    private static void AssertNear(Vector2 expected, Vector2 actual) =>
        Assert.True(Vector2.Distance(expected, actual) < 1e-5f, $"Expected {expected} but got {actual}.");

    private static void AssertNear(Vector4 expected, Vector4 actual) =>
        Assert.True(Vector4.Distance(expected, actual) < 1e-5f, $"Expected {expected} but got {actual}.");

    /// <summary>A grid that is neither square nor hex.</summary>
    private sealed class OtherGrid : IGrid<Cell>
    {
        public GridPlacement Placement { get; set; }

        public Cell CellAt(Vector3 world) => default;

        public Cell? CellAt(Ray ray) => null;

        public Vector3 Center(Cell cell) => Vector3.Zero;

        public void Corners(Cell cell, Span<Vector3> corners)
        {
        }

        public Cell Mirror(Cell cell, Cell around) => cell;

        public IGrid<Cell> MovedTo(GridPlacement placement) => this;
    }
}
