using System.Numerics;

namespace Super;

/// <summary>Any grid, square or hex: where it sits. See <see cref="IGrid{TCell}"/> for its cells.</summary>
public interface IGrid
{
    /// <summary>Where the grid sits. Set it again to move the grid with whatever it lies on.</summary>
    GridPlacement Placement { get; set; }
}

/// <summary>
/// What <see cref="SquareGrid"/> and <see cref="HexGrid"/> share, so code such as <see cref="LayeredGrid{TCell}"/> and
/// debug drawing works on either. See docs/grids.md.
/// </summary>
public interface IGrid<TCell> : IGrid
    where TCell : struct, IGridCell<TCell>
{
    /// <summary>The cell a world position lies over or under.</summary>
    TCell CellAt(Vector3 world);

    /// <summary>The cell a ray meets the grid in, or nothing when it misses the grid's plane.</summary>
    TCell? CellAt(Ray ray);

    /// <summary>The world position of a cell's middle, on the grid.</summary>
    Vector3 Center(TCell cell);

    /// <summary>A cell's corners in the world, 4 for a square and 6 for a hexagon, going round.</summary>
    void Corners(TCell cell, Span<Vector3> corners);

    /// <summary>A cell flipped across <paramref name="around"/> along the grid's X, as a model scaled by −1 along X is.</summary>
    TCell Mirror(TCell cell, TCell around);

    /// <summary>A grid the same as this one, sitting at <paramref name="placement"/> instead.</summary>
    IGrid<TCell> MovedTo(GridPlacement placement);
}
