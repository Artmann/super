using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;
using Rendition.Passes;

namespace Super;

/// <summary>
/// Grids and cells laid onto the ground for the player, for this frame only: a build grid round the mouse, the cell it's
/// over, where a unit can walk. Unlike <see cref="View.Debug"/>, they're part of the game's look. They're drawn
/// unlit over the finished picture and follow the ground's shape, so they read the same in any light. Works on a
/// <see cref="SquareGrid"/> or a <see cref="HexGrid"/>. See docs/grids.md.
/// </summary>
public sealed class ViewOverlays
{
    private readonly List<GridOverlayDraw> _drawn = [];
    private readonly List<uint> _words = [];
    private readonly List<(int A, int B)> _pairs = [];

    internal ViewOverlays()
    {
    }

    /// <summary>How many grid overlays this frame draws so far.</summary>
    public int Count => _drawn.Count;

    /// <summary>The overlays as the renderer draws them.</summary>
    internal ReadOnlySpan<GridOverlayDraw> Drawn => CollectionsMarshal.AsSpan(_drawn);

    /// <summary>The bits of the cells the overlays cover, 32 to a word.</summary>
    internal ReadOnlySpan<uint> Words => CollectionsMarshal.AsSpan(_words);

    /// <summary>
    /// The lines of every cell within <paramref name="steps"/> of <paramref name="center"/>: a square of cells for
    /// squares and a big hexagon for hexes.
    /// </summary>
    public void Grid<TCell>(IGrid<TCell> grid, TCell center, int steps, in GridLines lines)
        where TCell : struct, IGridCell<TCell>
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        _pairs.Clear();
        var (a, b) = center.Pair;
        for (int z = -steps; z <= steps; z++)
        {
            for (int x = -steps; x <= steps; x++)
            {
                var cell = TCell.FromPair(a + x, b + z);
                if (TCell.Steps(center, cell) <= steps)
                {
                    _pairs.Add(cell.Pair);
                }
            }
        }

        var focus = lines.Focus is { } world ? grid.Placement.OnGrid(world) : Vector2.Zero;
        Add(grid, new GridOverlayDraw
        {
            LineColor = Rgba(lines.Color),
            LineWidth = lines.Width,
            Focus = focus,
            FocusRadius = lines.Focus is null ? 0f : lines.FocusRadius,
            Depth = lines.Depth,
        });
    }

    /// <summary>Fills the cells, such as a path, a footprint or a flood fill, with a border round the edge of them.</summary>
    public void Cells<TCell>(IGrid<TCell> grid, ReadOnlySpan<TCell> cells, in CellFill fill)
        where TCell : struct, IGridCell<TCell>
    {
        ArgumentNullException.ThrowIfNull(grid);
        _pairs.Clear();
        foreach (var cell in cells)
        {
            _pairs.Add(cell.Pair);
        }

        Add(grid, Filled(fill));
    }

    /// <summary>Fills the cells, such as the keys of a <c>Reachable</c> answer, with a border round the edge of them.</summary>
    public void Cells<TCell>(IGrid<TCell> grid, IEnumerable<TCell> cells, in CellFill fill)
        where TCell : struct, IGridCell<TCell>
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(cells);
        _pairs.Clear();
        foreach (var cell in cells)
        {
            _pairs.Add(cell.Pair);
        }

        Add(grid, Filled(fill));
    }

    /// <summary>Fills one cell, such as the one under the mouse, with a border round it.</summary>
    public void Cell<TCell>(IGrid<TCell> grid, TCell cell, in CellFill fill)
        where TCell : struct, IGridCell<TCell> =>
        Cells(grid, [cell], fill);

    internal void Clear()
    {
        _drawn.Clear();
        _words.Clear();
    }

    private static GridOverlayDraw Filled(in CellFill fill) => new()
    {
        FillColor = Rgba(fill.Color),
        Softness = fill.Softness,
        BorderColor = Rgba(fill.BorderColor ?? fill.Color with { A = 1f }),
        BorderWidth = fill.Border,
        Depth = fill.Depth,
    };

    private static Vector4 Rgba(Color color) => new(color.R, color.G, color.B, color.A);

    /// <summary>Lays the collected cells into a window of bits and adds the overlay, filled in with the grid's shape.</summary>
    private void Add<TCell>(IGrid<TCell> grid, GridOverlayDraw overlay)
        where TCell : struct, IGridCell<TCell>
    {
        if (_drawn.Count == GridOverlayBatch.MaxOverlays)
        {
            throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                $"A frame can draw at most {GridOverlayBatch.MaxOverlays} grid overlays. Put cells that look the same in one."));
        }

        (overlay.Shape, overlay.CellSize) = Shape(grid);
        overlay.Grid = grid.Placement.Matrix;
        overlay.Cells = Window();
        GridOverlayBatch.Check(overlay, _words.Count + overlay.Cells.Words);
        if (_pairs.Count == 0)
        {
            return;
        }

        var bits = CollectionsMarshal.AsSpan(_words).Length;
        CollectionsMarshal.SetCount(_words, bits + overlay.Cells.Words);
        var words = CollectionsMarshal.AsSpan(_words)[bits..];
        words.Clear();
        var (first, columns) = (overlay.Cells.First, overlay.Cells.Columns);
        foreach (var (a, b) in _pairs)
        {
            int index = (a - first.A) + (b - first.B) * columns;
            words[index >> 5] |= 1u << (index & 31);
        }

        _drawn.Add(overlay);
    }

    /// <summary>The smallest window round the collected cells, starting after the words already used.</summary>
    private GridCellWindow Window()
    {
        if (_pairs.Count == 0)
        {
            return new GridCellWindow((0, 0), 0, 0, _words.Count);
        }

        var (low, high) = (_pairs[0], _pairs[0]);
        foreach (var (a, b) in _pairs)
        {
            low = (Math.Min(low.A, a), Math.Min(low.B, b));
            high = (Math.Max(high.A, a), Math.Max(high.B, b));
        }

        long columns = (long)high.A - low.A + 1;
        long rows = (long)high.B - low.B + 1;
        long words = (columns * rows + 31) / 32;
        if (_words.Count + words > GridOverlayBatch.MaxCellWords)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"A frame's grid overlays can cover at most {GridOverlayBatch.MaxCellWords * 32} cells between them, counting the gaps between each one's farthest cells, but these cells reach {columns} by {rows}."),
                "cells");
        }

        return new GridCellWindow(low, (int)columns, (int)rows, _words.Count);
    }

    private static (GridShape Shape, Vector2 CellSize) Shape<TCell>(IGrid<TCell> grid)
        where TCell : struct, IGridCell<TCell> => grid switch
        {
            SquareGrid square => (GridShape.Square, square.CellSize),
            HexGrid hex => (hex.Top == HexTop.Pointy ? GridShape.PointyHex : GridShape.FlatHex, new Vector2(hex.Size)),
            _ => throw new NotSupportedException($"Grid overlays draw on a SquareGrid or a HexGrid, not a {grid.GetType().Name}."),
        };
}
