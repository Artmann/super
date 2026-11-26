using System.Numerics;

namespace Super;

/// <summary>
/// How <see cref="ViewOverlays.Grid{TCell}"/> draws a grid's lines: their colour and width, and how far round a focus
/// point they reach before fading out. See docs/grids.md.
/// </summary>
/// <param name="Color">The lines' colour, in sRGB, with their opacity.</param>
public readonly record struct GridLines(Color Color)
{
    /// <summary>How wide the lines are, in metres on the grid. The default is 4 centimetres.</summary>
    public float Width { get; init; } = 0.04f;

    /// <summary>
    /// Where the lines are strongest, such as under the mouse, in the world. They fade out over
    /// <see cref="FocusRadius"/> from it. Null, the default, draws them all at full strength.
    /// </summary>
    public Vector3? Focus { get; init; }

    /// <summary>How far from <see cref="Focus"/> the lines reach, in metres. The default is 6.</summary>
    public float FocusRadius { get; init; } = 6f;

    /// <summary>
    /// How far above and below the grid the overlay lies on what's there, in metres. Ground on a hill this far up or
    /// down still gets it. The default is 1.
    /// </summary>
    public float Depth { get; init; } = 1f;
}

/// <summary>
/// How <see cref="ViewOverlays.Cells{TCell}(IGrid{TCell}, ReadOnlySpan{TCell}, in CellFill)"/> fills cells: their
/// colour, how softly the fill fades in from the edge of the cells, and the border round them. See docs/grids.md.
/// </summary>
/// <param name="Color">The fill's colour, in sRGB, with its opacity.</param>
public readonly record struct CellFill(Color Color)
{
    /// <summary>How far in from the edge of the cells the fill fades in, in metres. The default is 10 centimetres; 0 is a hard edge.</summary>
    public float Softness { get; init; } = 0.1f;

    /// <summary>How wide the border just inside the edge of the cells is, in metres. The default is 5 centimetres; 0 draws none.</summary>
    public float Border { get; init; } = 0.05f;

    /// <summary>The border's colour. Null, the default, is the fill's colour at full opacity.</summary>
    public Color? BorderColor { get; init; }

    /// <summary>How far above and below the grid the fill lies on what's there, in metres. The default is 1.</summary>
    public float Depth { get; init; } = 1f;
}
