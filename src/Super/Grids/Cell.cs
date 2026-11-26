namespace Super;

/// <summary>
/// A square on a <see cref="SquareGrid"/>: its column along the grid's X and its row along its Z. Cells compare and hash
/// by value, so they work as dictionary keys. See docs/grids.md.
/// </summary>
public readonly record struct Cell(int X, int Z) : IGridCell<Cell>
{
    private static readonly Cell[] _sides = [new(1, 0), new(0, 1), new(-1, 0), new(0, -1)];
    private static readonly Cell[] _around = [new(1, 0), new(1, 1), new(0, 1), new(-1, 1), new(-1, 0), new(-1, -1), new(0, -1), new(1, -1)];

    /// <summary>The four steps to the cells that share a side, going round from +X towards +Z.</summary>
    public static ReadOnlySpan<Cell> Sides => _sides;

    /// <summary>The eight steps to the cells that share a side or a corner, going round from +X towards +Z.</summary>
    public static ReadOnlySpan<Cell> Around => _around;

    public static Cell operator +(Cell a, Cell b) => new(a.X + b.X, a.Z + b.Z);

    public static Cell operator -(Cell a, Cell b) => new(a.X - b.X, a.Z - b.Z);

    /// <summary>How many steps apart two cells are when moving only across sides, as a rook would.</summary>
    public static int Manhattan(Cell a, Cell b) => Math.Abs(a.X - b.X) + Math.Abs(a.Z - b.Z);

    /// <summary>How many steps apart two cells are when corners count as steps too, as a king would move.</summary>
    public static int Chebyshev(Cell a, Cell b) => Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Z - b.Z));

    /// <summary>
    /// The cells a straight line from the middle of <paramref name="from"/> to the middle of <paramref name="to"/>
    /// passes nearest, from one end to the other. Each step goes to a cell in <see cref="Around"/>, so there is one
    /// more cell than <see cref="Chebyshev"/> steps.
    /// </summary>
    public static IEnumerable<Cell> Line(Cell from, Cell to)
    {
        int steps = Chebyshev(from, to);
        for (int step = 0; step <= steps; step++)
        {
            float t = steps == 0 ? 0f : step / (float)steps;
            // A nudge keeps a line that runs exactly between two cells from flipping between them.
            yield return new Cell(Nearest(from.X, to.X, t, 1e-4f), Nearest(from.Z, to.Z, t, 2e-4f));
        }
    }

    /// <summary>Every cell at most <paramref name="steps"/> king's steps from <paramref name="center"/>, a square of them, row by row.</summary>
    public static IEnumerable<Cell> Range(Cell center, int steps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        for (int z = -steps; z <= steps; z++)
        {
            for (int x = -steps; x <= steps; x++)
            {
                yield return new Cell(center.X + x, center.Z + z);
            }
        }
    }

    /// <summary>
    /// The cells exactly <paramref name="steps"/> king's steps from <paramref name="center"/>, the edge of a square,
    /// going round from the +X side towards +Z. A ring of 0 steps is the centre alone.
    /// </summary>
    public static IEnumerable<Cell> Ring(Cell center, int steps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        if (steps == 0)
        {
            yield return center;
            yield break;
        }

        // Starts at the corner below +X and walks each side in turn, each side 2 * steps long.
        var cell = new Cell(center.X + steps, center.Z - steps);
        for (int edge = 1; edge <= 4; edge++)
        {
            var side = _sides[edge % 4];
            for (int i = 0; i < steps * 2; i++)
            {
                yield return cell;
                cell += side;
            }
        }
    }

    /// <summary>Every cell at most <paramref name="steps"/> from <paramref name="center"/>, ring by ring outwards.</summary>
    public static IEnumerable<Cell> Spiral(Cell center, int steps)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        for (int ring = 0; ring <= steps; ring++)
        {
            foreach (var cell in Ring(center, ring))
            {
                yield return cell;
            }
        }
    }

    /// <summary>
    /// Turns a cell round another by quarter turns, the same way as <see cref="System.Numerics.Quaternion"/> turns
    /// about +Y by a positive angle: one turn takes +X to −Z. Negative turns go the other way.
    /// </summary>
    public static Cell Rotate(Cell cell, Cell around, int quarterTurns)
    {
        var offset = cell - around;
        for (int turn = 0; turn < ((quarterTurns % 4) + 4) % 4; turn++)
        {
            offset = new Cell(offset.Z, -offset.X);
        }

        return around + offset;
    }

    /// <summary>
    /// A cell flipped across the column of <paramref name="around"/>, so X runs the other way, as a model scaled by −1
    /// along X is. Mirroring twice gives the cell back.
    /// </summary>
    public static Cell Mirror(Cell cell, Cell around) => new(2 * around.X - cell.X, cell.Z);

    /// <summary>
    /// Every cell a walk from <paramref name="start"/> can reach without spending more than <paramref name="budget"/>,
    /// with the least it costs to get there. <paramref name="cost"/> says what stepping into a cell costs, or null
    /// where nothing can go. The start costs nothing. With <paramref name="corners"/>, steps across corners cost the
    /// same as steps across sides, as a king moves, and may pass between two blocked cells.
    /// </summary>
    public static IReadOnlyDictionary<Cell, float> Reachable(Cell start, float budget, Func<Cell, float?> cost, bool corners = false) =>
        GridSearch.Reachable(start, budget, cost, corners ? _around : _sides);

    (int A, int B) IGridCell<Cell>.Pair => (X, Z);

    static Cell IGridCell<Cell>.FromPair(int a, int b) => new(a, b);

    private static int Nearest(int from, int to, float t, float nudge) =>
        (int)MathF.Round(from + (to - from) * t + nudge, MidpointRounding.AwayFromZero);
}
