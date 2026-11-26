using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition.Passes;

/// <summary>
/// Checks a frame's grid overlays and packs them for <see cref="GridOverlayPass"/>: a box round each one's cells, how
/// the screen goes back into it, and its look.
/// </summary>
public static class GridOverlayBatch
{
    /// <summary>How many grid overlays one frame can draw. More throws.</summary>
    public const int MaxOverlays = 64;

    /// <summary>How many words of cell bits one frame can hold, 32 cells each. More throws.</summary>
    public const int MaxCellWords = 1 << 16;

    private static readonly float Root3 = MathF.Sqrt(3f);

    /// <summary>Throws when an overlay can't be drawn, naming what's wrong, or when its cells run past <paramref name="cellWords"/>.</summary>
    public static void Check(in GridOverlayDraw overlay, int cellWords)
    {
        if (!Matrix4x4.Invert(overlay.Grid, out _) || !DecalBatch.IsFinite(overlay.Grid))
        {
            throw new ArgumentException(
                "The grid overlay's grid matrix can't be inverted, or holds a number that isn't finite.", nameof(overlay));
        }

        if (!Enum.IsDefined(overlay.Shape))
        {
            throw new ArgumentException($"A grid overlay's shape is square or hex, but it is {(int)overlay.Shape}.", nameof(overlay));
        }

        float width = overlay.CellSize.X;
        float depth = overlay.Shape == GridShape.Square ? overlay.CellSize.Y : width;
        Positive(width, "cell size");
        Positive(depth, "cell size");
        Positive(overlay.Depth, "depth");
        NotNegative(overlay.LineWidth, "line width");
        NotNegative(overlay.Softness, "softness");
        NotNegative(overlay.BorderWidth, "border width");
        NotNegative(overlay.FocusRadius, "focus radius");
        CheckCells(overlay.Cells, cellWords);
    }

    /// <summary>
    /// Checks each overlay and writes it into <paramref name="into"/>, which must hold as many, seen through
    /// <paramref name="viewProjection"/>. Throws past <see cref="MaxOverlays"/> or <see cref="MaxCellWords"/>.
    /// </summary>
    internal static void Pack(ReadOnlySpan<GridOverlayDraw> overlays, int cellWords, in Matrix4x4 viewProjection, Span<GpuGridOverlay> into)
    {
        if (overlays.Length > MaxOverlays)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"A frame can draw at most {MaxOverlays} grid overlays, but it was given {overlays.Length}."), nameof(overlays));
        }

        if (cellWords > MaxCellWords)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"A frame's grid overlays can cover at most {MaxCellWords * 32} cells between them, but they were given {(long)cellWords * 32}."),
                nameof(cellWords));
        }

        Matrix4x4.Invert(viewProjection, out var fromScreen);
        for (int i = 0; i < overlays.Length; i++)
        {
            ref readonly var overlay = ref overlays[i];
            Check(overlay, cellWords);
            var (low, high) = Area(overlay);
            var box = Box(overlay, low, high);
            Matrix4x4.Invert(box, out var inverse);
            into[i] = new GpuGridOverlay
            {
                ToClip = box * viewProjection,
                FromClip = fromScreen * inverse,
                Area = new Vector4(low, high.X - low.X, high.Y - low.Y),
                Cell = new Vector4(overlay.CellSize, overlay.Depth, 0f),
                LineColor = overlay.LineColor,
                FillColor = overlay.FillColor,
                BorderColor = overlay.BorderColor,
                Widths = new Vector4(overlay.LineWidth, overlay.BorderWidth, overlay.Softness, overlay.FocusRadius),
                Focus = new Vector4(overlay.Focus, 0f, 0f),
                Window = new GridWindowInts(overlay.Cells),
                Start = overlay.Cells.Start,
                Shape = (int)overlay.Shape,
            };
        }
    }

    /// <summary>
    /// The part of the grid's X and Z the overlay's box covers: round its window's cells, and far enough past them for
    /// the lines on their outer edges.
    /// </summary>
    internal static (Vector2 Low, Vector2 High) Area(in GridOverlayDraw overlay)
    {
        var window = overlay.Cells;
        var (a, b) = window.First;
        int lastA = a + Math.Max(window.Columns, 1) - 1;
        int lastB = b + Math.Max(window.Rows, 1) - 1;
        Vector2 low, high;
        if (overlay.Shape == GridShape.Square)
        {
            low = new Vector2(a, b) * overlay.CellSize;
            high = new Vector2(lastA + 1, lastB + 1) * overlay.CellSize;
        }
        else
        {
            var corners = (Middle(overlay, a, b), Middle(overlay, lastA, b), Middle(overlay, a, lastB), Middle(overlay, lastA, lastB));
            low = Vector2.Min(Vector2.Min(corners.Item1, corners.Item2), Vector2.Min(corners.Item3, corners.Item4)) - new Vector2(overlay.CellSize.X);
            high = Vector2.Max(Vector2.Max(corners.Item1, corners.Item2), Vector2.Max(corners.Item3, corners.Item4)) + new Vector2(overlay.CellSize.X);
        }

        float margin = Math.Max(overlay.LineWidth, overlay.BorderWidth) + overlay.CellSize.X * 0.25f;
        return (low - new Vector2(margin), high + new Vector2(margin));
    }

    /// <summary>The cube from −0.5 to 0.5 stretched over the area and the depth above and below it, on the grid.</summary>
    internal static Matrix4x4 Box(in GridOverlayDraw overlay, Vector2 low, Vector2 high)
    {
        var size = high - low;
        var middle = (low + high) / 2f;
        return Matrix4x4.CreateScale(size.X, overlay.Depth * 2f, size.Y)
            * Matrix4x4.CreateTranslation(middle.X, 0f, middle.Y)
            * overlay.Grid;
    }

    /// <summary>A hexagon's middle on the grid, as HexGrid places it.</summary>
    private static Vector2 Middle(in GridOverlayDraw overlay, int q, int r) => overlay.Shape == GridShape.PointyHex
        ? new Vector2(Root3 * q + Root3 / 2f * r, 1.5f * r) * overlay.CellSize.X
        : new Vector2(1.5f * q, Root3 / 2f * q + Root3 * r) * overlay.CellSize.X;

    private static void CheckCells(GridCellWindow window, int cellWords)
    {
        if (window.Columns < 0 || window.Rows < 0 || window.Start < 0 || (long)window.Start + window.Words > cellWords)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"The grid overlay's cells, {window.Columns} by {window.Rows} from word {window.Start}, run past the frame's {cellWords} words of cells."),
                "overlay");
        }
    }

    private static void Positive(float value, string name)
    {
        if (!(float.IsFinite(value) && value > 0f))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"A grid overlay's {name} must be more than 0, but it is {value}."), "overlay");
        }
    }

    private static void NotNegative(float value, string name)
    {
        if (!(float.IsFinite(value) && value >= 0f))
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"A grid overlay's {name} must be 0 or more, but it is {value}."), "overlay");
        }
    }
}

/// <summary>A cell window as four whole numbers, as the shader reads it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal readonly struct GridWindowInts(GridCellWindow window)
{
    public readonly int FirstA = window.First.A;
    public readonly int FirstB = window.First.B;
    public readonly int Columns = window.Columns;
    public readonly int Rows = window.Rows;
}

/// <summary>One grid overlay as the shaders read it. Must match grid_overlay.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuGridOverlay
{
    /// <summary>The overlay's box to the screen.</summary>
    public Matrix4x4 ToClip;

    /// <summary>The screen back into the overlay's box.</summary>
    public Matrix4x4 FromClip;

    /// <summary>The box's lowest corner on the grid's X and Z, then its size.</summary>
    public Vector4 Area;

    /// <summary>The cell size, then the depth each way.</summary>
    public Vector4 Cell;
    public Vector4 LineColor;
    public Vector4 FillColor;
    public Vector4 BorderColor;

    /// <summary>The line width, the border width, the softness and the focus radius.</summary>
    public Vector4 Widths;
    public Vector4 Focus;
    public GridWindowInts Window;
    public int Start;
    public int Shape;
    private int _pad0;
    private int _pad1;
}
