using System.Globalization;
using System.Numerics;

namespace Super;

/// <summary>
/// A grid of rectangular cells lying on its <see cref="Placement"/>, such as a building grid or the tiles of a dungeon
/// level. Cell 0, 0 runs from the grid's origin to one cell size along its X and Z. A game can have as many as it
/// needs, each with its own size and placement. See docs/grids.md.
/// </summary>
public sealed class SquareGrid
{
    /// <summary>A grid of cells <paramref name="cellSize"/> wide along X and deep along Z, in metres.</summary>
    public SquareGrid(Vector2 cellSize, GridPlacement placement = default)
    {
        if (!(float.IsFinite(cellSize.X) && float.IsFinite(cellSize.Y) && cellSize.X > 0f && cellSize.Y > 0f))
        {
            throw new ArgumentOutOfRangeException(
                nameof(cellSize),
                string.Create(CultureInfo.InvariantCulture, $"A square grid's cells must be more than 0 metres on each side, but they are {cellSize.X} by {cellSize.Y}."));
        }

        CellSize = cellSize;
        Placement = placement;
    }

    /// <summary>A grid of square cells <paramref name="cellSize"/> metres on each side.</summary>
    public SquareGrid(float cellSize, GridPlacement placement = default)
        : this(new Vector2(cellSize), placement)
    {
    }

    /// <summary>How wide each cell is along the grid's X, and how deep along its Z, in metres.</summary>
    public Vector2 CellSize { get; }

    /// <summary>Where the grid sits. Set it again to move the grid with whatever it lies on.</summary>
    public GridPlacement Placement { get; set; }

    /// <summary>The cell a world position lies over or under.</summary>
    public Cell CellAt(Vector3 world) => CellOn(Placement.OnGrid(world));

    /// <summary>The cell a ray, such as the mouse's, meets the grid in, or nothing when it misses the grid's plane.</summary>
    public Cell? CellAt(Ray ray) => Placement.Hit(ray) is { } onGrid ? CellOn(onGrid) : null;

    /// <summary>The world position of a cell's middle, on the grid.</summary>
    public Vector3 Center(Cell cell) => Placement.InWorld((new Vector2(cell.X, cell.Z) + new Vector2(0.5f)) * CellSize);

    /// <summary>A cell's four corners in the world, going round from the grid's origin side towards +X, then +Z.</summary>
    public void Corners(Cell cell, Span<Vector3> corners)
    {
        if (corners.Length < 4)
        {
            throw new ArgumentException($"A square cell has 4 corners, but there is room for {corners.Length}.", nameof(corners));
        }

        var low = new Vector2(cell.X, cell.Z) * CellSize;
        corners[0] = Placement.InWorld(low);
        corners[1] = Placement.InWorld(low + new Vector2(CellSize.X, 0f));
        corners[2] = Placement.InWorld(low + CellSize);
        corners[3] = Placement.InWorld(low + new Vector2(0f, CellSize.Y));
    }

    private Cell CellOn(Vector2 onGrid)
    {
        var cells = onGrid / CellSize;
        return new Cell((int)MathF.Floor(cells.X), (int)MathF.Floor(cells.Y));
    }
}
