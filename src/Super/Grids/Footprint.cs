namespace Super;

/// <summary>
/// The cells something covers when it takes up more than one, such as a 2 by 3 barn or a temple of three hexes, around
/// its own cell 0, 0. Place it at a cell with a turn to find the cells it covers there, and ask whether it fits. See
/// docs/grids.md.
/// </summary>
public sealed class Footprint<TCell>
    where TCell : struct, IGridCell<TCell>
{
    private readonly TCell[] _cells;

    /// <summary>A footprint of these cells, each once, measured from the thing's own cell 0, 0.</summary>
    public Footprint(IEnumerable<TCell> cells)
    {
        ArgumentNullException.ThrowIfNull(cells);
        var seen = new HashSet<TCell>();
        var kept = new List<TCell>();
        foreach (var cell in cells)
        {
            if (!seen.Add(cell))
            {
                throw new ArgumentException($"A footprint lists each cell once, but {cell} is there twice.", nameof(cells));
            }

            kept.Add(cell);
        }

        if (kept.Count == 0)
        {
            throw new ArgumentException("A footprint must cover at least one cell.", nameof(cells));
        }

        _cells = [.. kept];
    }

    /// <summary>The cells, measured from the thing's own cell 0, 0, with no turn.</summary>
    public ReadOnlySpan<TCell> Cells => _cells;

    /// <summary>How many cells the footprint covers.</summary>
    public int Count => _cells.Length;

    /// <summary>
    /// The cells the footprint covers with its cell 0, 0 at <paramref name="at"/> and turned round it, by quarter turns
    /// for squares and sixth turns for hexes, the way <see cref="Cell.Rotate"/> and <see cref="Hex.Rotate"/> turn. Writes
    /// them into <paramref name="cells"/>, which needs room for <see cref="Count"/>, so placing a ghost each frame
    /// allocates nothing.
    /// </summary>
    public void Placed(TCell at, int turns, Span<TCell> cells)
    {
        if (cells.Length < _cells.Length)
        {
            throw new ArgumentException($"The footprint covers {_cells.Length} cells, but there is room for {cells.Length}.", nameof(cells));
        }

        for (int i = 0; i < _cells.Length; i++)
        {
            cells[i] = TCell.Rotate(_cells[i], default, turns) + at;
        }
    }

    /// <summary>
    /// Whether every cell the footprint covers at <paramref name="at"/>, turned by <paramref name="turns"/>, is free, as
    /// <paramref name="isFree"/> says. Use the answer to draw a building's ghost green or red.
    /// </summary>
    public bool Fits(TCell at, int turns, Func<TCell, bool> isFree)
    {
        ArgumentNullException.ThrowIfNull(isFree);
        foreach (var cell in _cells)
        {
            if (!isFree(TCell.Rotate(cell, default, turns) + at))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The footprint flipped across its cell 0, 0 along the grid's X, for a thing drawn mirrored.</summary>
    public Footprint<TCell> Mirrored(IGrid<TCell> grid)
    {
        ArgumentNullException.ThrowIfNull(grid);
        return new Footprint<TCell>(_cells.Select(cell => grid.Mirror(cell, default)));
    }
}

/// <summary>Ready-made footprints. See <see cref="Footprint{TCell}"/>.</summary>
public static class Footprints
{
    /// <summary>A footprint of these cells.</summary>
    public static Footprint<TCell> Of<TCell>(params TCell[] cells)
        where TCell : struct, IGridCell<TCell> => new(cells);

    /// <summary>A rectangle of square cells <paramref name="width"/> along X and <paramref name="depth"/> along Z, from cell 0, 0.</summary>
    public static Footprint<Cell> Rectangle(int width, int depth)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(depth);
        return new Footprint<Cell>(Enumerable.Range(0, depth).SelectMany(z => Enumerable.Range(0, width).Select(x => new Cell(x, z))));
    }

    /// <summary>Every hex at most <paramref name="steps"/> from hex 0, 0, a big hexagon around the middle.</summary>
    public static Footprint<Hex> Hexagon(int steps) => new(Hex.Range(default, steps));
}
