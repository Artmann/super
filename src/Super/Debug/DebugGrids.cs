using System.Globalization;
using System.Numerics;

namespace Super;

/// <summary>
/// Debug drawing for square and hex grids: outlines, coordinates, a heat map and highlighted cells, each in its own
/// channel so they show one at a time. An area is the cells within some steps of a centre: a square of them for
/// squares and a big hexagon for hexes. Hidden, a call costs only the check of its channel. See docs/grids.md.
/// </summary>
public static class DebugGrids
{
    /// <summary>The channel cell outlines are drawn in unless another is given.</summary>
    public const string Outlines = "grid";

    /// <summary>The channel cell coordinates are drawn in unless another is given.</summary>
    public const string Coordinates = "grid.labels";

    /// <summary>The channel heat maps are drawn in unless another is given. Name each one, such as <c>grid.heat:cost</c>.</summary>
    public const string Heat = "grid.heat";

    /// <summary>The most labels' text kept for reuse, so drawing them each frame doesn't allocate.</summary>
    private const int MostKeptText = 4096;

    private const int LegendPieces = 8;

    /// <summary>How long the legend's bar is, in steps between cells.</summary>
    private const float LegendCells = 3f;

    private static readonly Color OutlineColor = new(1f, 1f, 1f, 0.6f);

    // Shrunk copies of each cell's outline, which read as a fill. Debug drawing only has lines.
    private static readonly float[] HeatRings = [0.9f, 0.7f, 0.5f, 0.3f, 0.12f];

    private static readonly float[] HighlightRings = [0.86f, 0.76f];

    private static readonly Dictionary<(int, int), string> PairText = [];

    private static readonly Dictionary<float, string> NumberText = [];

    /// <summary>The outline of every cell within <paramref name="steps"/> of <paramref name="center"/>.</summary>
    public static void Grid<TCell>(
        this DebugCanvas canvas, IGrid<TCell> grid, TCell center, int steps, Color? color = null, DebugLook? look = null)
        where TCell : struct, IGridCell<TCell>
    {
        var drawn = look ?? Outlines;
        if (!Begin(canvas, grid, steps, drawn))
        {
            return;
        }

        var pen = new Pen<TCell>(canvas, grid, drawn, stackalloc Vector3[TCell.Sides.Length]);
        foreach (var cell in new Area<TCell>(center, steps))
        {
            pen.Outline(cell, 1f, color ?? OutlineColor);
        }
    }

    /// <summary>The coordinates of every cell within <paramref name="steps"/> of <paramref name="center"/>, over its middle.</summary>
    public static void GridLabels<TCell>(
        this DebugCanvas canvas, IGrid<TCell> grid, TCell center, int steps, Color? color = null, DebugLook? look = null)
        where TCell : struct, IGridCell<TCell>
    {
        var drawn = look ?? Coordinates;
        if (!Begin(canvas, grid, steps, drawn))
        {
            return;
        }

        foreach (var cell in new Area<TCell>(center, steps))
        {
            canvas.Text(grid.Center(cell), TextOf(cell.Pair), color ?? Color.White, drawn);
        }
    }

    /// <summary>
    /// Colours every cell within <paramref name="steps"/> of <paramref name="center"/> by <paramref name="value"/> on
    /// <paramref name="scale"/>, leaving out cells whose value is null, with a legend past the area's +X side. With no
    /// scale, blue to red runs from the area's lowest value to its highest.
    /// </summary>
    public static void GridHeat<TCell>(
        this DebugCanvas canvas, IGrid<TCell> grid, TCell center, int steps, Func<TCell, float?> value,
        HeatScale? scale = null, DebugLook? look = null)
        where TCell : struct, IGridCell<TCell>
    {
        ArgumentNullException.ThrowIfNull(value);
        scale?.Check();
        var drawn = look ?? Heat;
        if (!Begin(canvas, grid, steps, drawn))
        {
            return;
        }

        var used = scale ?? Spread(new Area<TCell>(center, steps), value);
        var pen = new Pen<TCell>(canvas, grid, drawn, stackalloc Vector3[TCell.Sides.Length]);
        foreach (var cell in new Area<TCell>(center, steps))
        {
            if (Number(value(cell)) is { } heat)
            {
                pen.Fill(cell, used.At(heat));
            }
        }

        pen.Legend((center, steps), used);
    }

    /// <summary>Highlights each of the cells, such as a path, a footprint or a flood fill, inside its outline.</summary>
    public static void Cells<TCell>(this DebugCanvas canvas, IGrid<TCell> grid, ReadOnlySpan<TCell> cells, Color color, DebugLook look = default)
        where TCell : struct, IGridCell<TCell>
    {
        if (Begin(canvas, grid, 0, look))
        {
            new Pen<TCell>(canvas, grid, look, stackalloc Vector3[TCell.Sides.Length]).Highlight(cells, color);
        }
    }

    /// <summary>Highlights each of the cells, such as the keys of a <c>Reachable</c> answer, inside its outline.</summary>
    public static void Cells<TCell>(this DebugCanvas canvas, IGrid<TCell> grid, IEnumerable<TCell> cells, Color color, DebugLook look = default)
        where TCell : struct, IGridCell<TCell>
    {
        ArgumentNullException.ThrowIfNull(cells);
        if (Begin(canvas, grid, 0, look))
        {
            new Pen<TCell>(canvas, grid, look, stackalloc Vector3[TCell.Sides.Length]).Highlight(cells, color);
        }
    }

    /// <summary>Text for a pair of coordinates, such as "3, -2", made once and kept.</summary>
    internal static string TextOf((int A, int B) pair)
    {
        if (!PairText.TryGetValue(pair, out var text))
        {
            text = string.Create(CultureInfo.InvariantCulture, $"{pair.A}, {pair.B}");
            Keep(PairText, pair, text);
        }

        return text;
    }

    /// <summary>Text for a number, such as "2.5", made once and kept.</summary>
    internal static string TextOf(float number)
    {
        if (!NumberText.TryGetValue(number, out var text))
        {
            text = number.ToString("0.##", CultureInfo.InvariantCulture);
            Keep(NumberText, number, text);
        }

        return text;
    }

    private static void Keep<TKey>(Dictionary<TKey, string> kept, TKey key, string text)
        where TKey : notnull
    {
        if (kept.Count >= MostKeptText)
        {
            kept.Clear();
        }

        kept[key] = text;
    }

    /// <summary>Checks the arguments, then whether the look's channel is shown.</summary>
    private static bool Begin(DebugCanvas canvas, IGrid grid, int steps, in DebugLook look)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentOutOfRangeException.ThrowIfNegative(steps);
        return canvas.Showing(look.Channel);
    }

    /// <summary>A value as a number to colour by, or null for a cell left out.</summary>
    private static float? Number(float? value) => value is { } number && !float.IsNaN(number) ? number : null;

    /// <summary>A scale from the lowest value in the area to the highest, one wide when they're all the same.</summary>
    private static HeatScale Spread<TCell>(Area<TCell> area, Func<TCell, float?> value)
        where TCell : struct, IGridCell<TCell>
    {
        float low = float.PositiveInfinity;
        float high = float.NegativeInfinity;
        foreach (var cell in area)
        {
            if (Number(value(cell)) is { } number && float.IsFinite(number))
            {
                low = Math.Min(low, number);
                high = Math.Max(high, number);
            }
        }

        if (!float.IsFinite(low))
        {
            return new HeatScale(0f, 1f);
        }

        return new HeatScale(low, high > low ? high : low + 1f);
    }

    /// <summary>Draws shapes on cells of one grid in one look, with room for a cell's corners.</summary>
    private readonly ref struct Pen<TCell>(DebugCanvas canvas, IGrid<TCell> grid, DebugLook look, Span<Vector3> corners)
        where TCell : struct, IGridCell<TCell>
    {
        private readonly Span<Vector3> _corners = corners;

        /// <summary>A cell's outline, shrunk towards its middle by <paramref name="scale"/>.</summary>
        public void Outline(TCell cell, float scale, Color color)
        {
            grid.Corners(cell, _corners);
            if (scale != 1f)
            {
                var middle = grid.Center(cell);
                for (int i = 0; i < _corners.Length; i++)
                {
                    _corners[i] = middle + (_corners[i] - middle) * scale;
                }
            }

            canvas.Path(_corners, color, closed: true, look);
        }

        public void Fill(TCell cell, Color color)
        {
            foreach (float ring in HeatRings)
            {
                Outline(cell, ring, color);
            }
        }

        public void Highlight(ReadOnlySpan<TCell> cells, Color color)
        {
            foreach (var cell in cells)
            {
                Highlight(cell, color);
            }
        }

        public void Highlight(IEnumerable<TCell> cells, Color color)
        {
            foreach (var cell in cells)
            {
                Highlight(cell, color);
            }
        }

        private void Highlight(TCell cell, Color color)
        {
            foreach (float ring in HighlightRings)
            {
                Outline(cell, ring, color);
            }
        }

        /// <summary>A bar three cells long blending from cold to hot, a step past the area's last cell on its +X side, with the low and high numbers at its ends.</summary>
        public void Legend((TCell Center, int Steps) area, HeatScale scale)
        {
            var middle = grid.Center(area.Center);
            var step = grid.Center(area.Center + TCell.Sides[0]) - middle;
            var across = Vector3.Cross(step, grid.Placement.Up);
            var start = middle + step * (area.Steps + 1f) + across * (LegendCells / 2f);
            var end = start - across * LegendCells;
            var bar = look with { Thickness = 6f };
            for (int i = 0; i < LegendPieces; i++)
            {
                float t = (i + 0.5f) / LegendPieces;
                var color = scale.At(scale.Low + (scale.High - scale.Low) * t);
                canvas.Line(Vector3.Lerp(start, end, i / (float)LegendPieces), Vector3.Lerp(start, end, (i + 1) / (float)LegendPieces), color, bar);
            }

            canvas.Text(start, TextOf(scale.Low), scale.Cold, look);
            canvas.Text(end, TextOf(scale.High), scale.Hot, look);
        }
    }

    /// <summary>The cells within some steps of a centre, row by row, walked without allocating.</summary>
    private struct Area<TCell>(TCell center, int steps)
        where TCell : struct, IGridCell<TCell>
    {
        private readonly (int A, int B) _center = center.Pair;
        private int _a = -steps - 1;
        private int _b = -steps;

        public TCell Current { get; private set; }

        public readonly Area<TCell> GetEnumerator() => this;

        public bool MoveNext()
        {
            while (true)
            {
                if (++_a > steps)
                {
                    _a = -steps;
                    if (++_b > steps)
                    {
                        return false;
                    }
                }

                var cell = TCell.FromPair(_center.A + _a, _center.B + _b);
                if (TCell.Steps(center, cell) <= steps)
                {
                    Current = cell;
                    return true;
                }
            }
        }
    }
}
