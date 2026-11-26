namespace Super.Ui;

/// <summary>
/// A table of views. Give it <see cref="GridRow"/>s and each column is as wide as its widest cell, as a settings
/// screen lines its labels up; a column with a cell that stretches takes the width left over, and when there isn't
/// room the columns shrink alike. A child that isn't a row, such as a <see cref="Divider"/>, spans every column.
/// Without rows, <paramref name="Columns"/> equal columns are filled left to right and then down, and a null child
/// leaves its cell empty. A row is as tall as its tallest cell.
/// </summary>
public sealed record Grid(int Columns = 0, float ColumnGap = 8f, float? RowGap = null, HorizontalAlignment Alignment = HorizontalAlignment.Leading)
    : Container
{
    private float Gaps => ColumnGap * (Columns - 1);

    private float RowSpacing => RowGap ?? ColumnGap;

    internal override UiSize Measure(UiNode node, Proposal proposal)
    {
        if (HasRows(node))
        {
            return new UiGridRows(node, proposal.Width, ColumnGap).Size(RowSpacing);
        }

        float cell = CellWidth(node, proposal.Width);
        float height = 0f;
        foreach (float row in RowHeights(node, cell))
        {
            height += row + RowSpacing;
        }

        return new UiSize(cell * Columns + Gaps, Math.Max(height - RowSpacing, 0f));
    }

    internal override void Place(UiNode node, UiRect frame)
    {
        if (HasRows(node))
        {
            new UiGridRows(node, frame.Width, ColumnGap).Place(frame, RowSpacing, Alignment.Fraction());
            return;
        }

        float cell = CellWidth(node, frame.Width);
        float[] rows = RowHeights(node, cell);
        float y = frame.Y;
        for (int row = 0; row < rows.Length; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                int index = row * Columns + column;
                if (index < node.Children.Count && node.Children[index] is { } child)
                {
                    var size = child.Measure(new Proposal(cell, null));
                    float x = frame.X + column * (cell + ColumnGap) + (cell - size.Width) * Alignment.Fraction();
                    child.Place(new UiRect(x, y, size.Width, size.Height));
                }
            }

            y += rows[row] + RowSpacing;
        }
    }

    private bool HasRows(UiNode node)
    {
        if (node.Children.Any(child => child?.View is GridRow))
        {
            return true;
        }

        if (Columns <= 0)
        {
            throw new InvalidOperationException("A Grid needs Columns above zero, or GridRows that set the columns.");
        }

        return false;
    }

    private float CellWidth(UiNode node, float? offered)
    {
        if (offered is { } width && !float.IsInfinity(width))
        {
            return Math.Max((width - Gaps) / Columns, 0f);
        }

        float widest = 0f;
        foreach (var child in node.Children)
        {
            if (child is not null)
            {
                widest = Math.Max(widest, child.Measure(new Proposal(null, null)).Width);
            }
        }

        return widest;
    }

    private float[] RowHeights(UiNode node, float cell)
    {
        float[] rows = new float[(node.Children.Count + Columns - 1) / Columns];
        for (int i = 0; i < node.Children.Count; i++)
        {
            if (node.Children[i] is { } child)
            {
                rows[i / Columns] = Math.Max(rows[i / Columns], child.Measure(new Proposal(cell, null)).Height);
            }
        }

        return rows;
    }
}

/// <summary>One row of a <see cref="Grid"/>: each child is a cell in the next column, and a null leaves its cell empty.</summary>
public sealed record GridRow(VerticalAlignment Alignment = VerticalAlignment.Center) : Container
{
    // The grid lays a row's cells out, since only it knows the columns.
    internal override UiSize Measure(UiNode node, Proposal proposal) => default;

    internal override void Place(UiNode node, UiRect frame)
    {
    }
}

/// <summary>A grid of rows, measured for one width: how wide each column is and how tall each row is.</summary>
internal sealed class UiGridRows
{
    // How much wider a cell must get, offered all the room, to count as stretching.
    private const float Stretch = 0.5f;

    private readonly UiNode[] _rows;
    private readonly float[] _widths;
    private readonly float[] _heights;
    private readonly float _gap;

    public UiGridRows(UiNode grid, float? width, float gap)
    {
        _gap = gap;
        _rows = grid.Children.OfType<UiNode>().ToArray();
        int columns = _rows.Select(row => row.View is GridRow ? row.Children.Count : 0).DefaultIfEmpty(0).Max();
        _widths = new float[columns];
        var stretches = new bool[columns];
        foreach (var row in _rows.Where(row => row.View is GridRow))
        {
            for (int column = 0; column < row.Children.Count; column++)
            {
                if (row.Children[column] is { } cell)
                {
                    float ideal = cell.Measure(new Proposal(null, null)).Width;
                    _widths[column] = Math.Max(_widths[column], ideal);
                    stretches[column] |= cell.Measure(new Proposal(float.PositiveInfinity, null)).Width > ideal + Stretch;
                }
            }
        }

        if (width is { } offered && float.IsFinite(offered))
        {
            Fit(offered - gap * Math.Max(columns - 1, 0), stretches);
        }

        _heights = _rows.Select(Height).ToArray();
    }

    private float Width => _widths.Sum() + _gap * Math.Max(_widths.Length - 1, 0);

    public UiSize Size(float rowGap) => new(Width, _heights.Sum() + rowGap * Math.Max(_heights.Length - 1, 0));

    public void Place(UiRect frame, float rowGap, float alignment)
    {
        float y = frame.Y;
        for (int i = 0; i < _rows.Length; i++)
        {
            var row = _rows[i];
            if (row.View is GridRow { Alignment: var vertical })
            {
                var line = new UiRect(frame.X, y, Width, _heights[i]);
                row.Place(line);
                PlaceCells(row, line, alignment, vertical.Fraction());
            }
            else
            {
                var size = row.Measure(new Proposal(Width, null));
                row.Place(new UiRect(frame.X + (Width - size.Width) * alignment, y, size.Width, size.Height));
            }

            y += _heights[i] + rowGap;
        }
    }

    private void PlaceCells(UiNode row, UiRect frame, float alignment, float vertical)
    {
        float x = frame.X;
        for (int column = 0; column < _widths.Length; column++)
        {
            if (column < row.Children.Count && row.Children[column] is { } cell)
            {
                var size = cell.Measure(new Proposal(_widths[column], null));
                cell.Place(new UiRect(x + (_widths[column] - size.Width) * alignment, frame.Y + (frame.Height - size.Height) * vertical, size.Width, size.Height));
            }

            x += _widths[column] + _gap;
        }
    }

    /// <summary>Hands the room left over to the columns that stretch, or shrinks every column alike when there's too little.</summary>
    private void Fit(float room, bool[] stretches)
    {
        float total = _widths.Sum();
        int stretching = stretches.Count(stretch => stretch);
        if (total < room && stretching > 0)
        {
            float extra = (room - total) / stretching;
            for (int column = 0; column < _widths.Length; column++)
            {
                _widths[column] += stretches[column] ? extra : 0f;
            }
        }
        else if (total > room && total > 0f)
        {
            float scale = Math.Max(room, 0f) / total;
            for (int column = 0; column < _widths.Length; column++)
            {
                _widths[column] *= scale;
            }
        }
    }

    private float Height(UiNode row)
    {
        if (row.View is not GridRow)
        {
            return row.Measure(new Proposal(Width, null)).Height;
        }

        float tallest = 0f;
        for (int column = 0; column < row.Children.Count; column++)
        {
            if (row.Children[column] is { } cell)
            {
                tallest = Math.Max(tallest, cell.Measure(new Proposal(_widths[column], null)).Height);
            }
        }

        return tallest;
    }
}
