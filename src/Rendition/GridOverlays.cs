using System.Numerics;

namespace Rendition;

/// <summary>The shape of a grid's cells, for <see cref="GridOverlayDraw"/>.</summary>
public enum GridShape
{
    /// <summary>Rectangles. Cell X, Z runs from X, Z times the cell size to one cell size further along each.</summary>
    Square,

    /// <summary>Hexagons with a corner pointing along the grid's Z, numbered Q along X and R towards +Z.</summary>
    PointyHex,

    /// <summary>Hexagons with a side facing along the grid's Z, numbered Q along X and R towards +Z.</summary>
    FlatHex,
}

/// <summary>
/// Which of a grid's cells an overlay covers: a window of cells, <see cref="Columns"/> by <see cref="Rows"/> from
/// <see cref="First"/>, with one bit a cell in <see cref="FrameExtras.GridCells"/>, row by row from word
/// <see cref="Start"/>. A cell's first number picks its column and its second its row.
/// </summary>
/// <param name="First">The cell in the window's first column and row: X and Z for squares, Q and R for hexes.</param>
/// <param name="Columns">How many cells the window has along the first number.</param>
/// <param name="Rows">How many cells the window has along the second number.</param>
/// <param name="Start">The first word of the window's bits. Bit 0 of a word is its first cell.</param>
public readonly record struct GridCellWindow((int A, int B) First, int Columns, int Rows, int Start)
{
    /// <summary>How many words of bits the window takes.</summary>
    public int Words => (int)(((long)Columns * Rows + 31) / 32);
}

/// <summary>
/// Cells of a grid laid onto whatever the camera sees over and under them, drawn over the finished picture without
/// light, as a building game shows its grid, the cell under the mouse or where a house would go. Lines run along
/// every edge of the covered cells, the fill colours them in, soft where the covered cells end, and the border runs
/// round their outside. Each part with no width or no opacity is left out. Surfaces that turn away from the grid's up,
/// such as walls, take less of it, and none past 60 degrees. See "Grid overlays" in docs/design.md.
/// </summary>
public struct GridOverlayDraw
{
    /// <summary>Takes the grid's own X, up and Z, in metres, to the world: its turn, then its origin.</summary>
    public Matrix4x4 Grid;

    public GridShape Shape;

    /// <summary>A square cell's width along X and depth along Z, or a hexagon's middle to a corner in X.</summary>
    public Vector2 CellSize;

    /// <summary>Which cells are covered.</summary>
    public GridCellWindow Cells;

    /// <summary>How far above and below the grid the overlay reaches, in metres, so it lies on hills.</summary>
    public float Depth;

    /// <summary>The lines' colour, in sRGB, with its opacity.</summary>
    public Vector4 LineColor;

    /// <summary>How wide the lines are, in metres.</summary>
    public float LineWidth;

    /// <summary>The fill's colour, in sRGB, with its opacity.</summary>
    public Vector4 FillColor;

    /// <summary>How far in from the edge of the covered cells the fill fades in, in metres. 0 is a hard edge.</summary>
    public float Softness;

    /// <summary>The border's colour, in sRGB, with its opacity.</summary>
    public Vector4 BorderColor;

    /// <summary>How wide the border is, in metres, inside the covered cells.</summary>
    public float BorderWidth;

    /// <summary>Where on the grid's X and Z the lines are strongest when <see cref="FocusRadius"/> is more than 0.</summary>
    public Vector2 Focus;

    /// <summary>How far from <see cref="Focus"/> the lines fade out, in metres, or 0 for lines all over.</summary>
    public float FocusRadius;
}
