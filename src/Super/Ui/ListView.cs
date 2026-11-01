namespace Super.Ui;

/// <summary>
/// A scrolling list you can pick from. <paramref name="Row"/> describes an item, and this
/// handles how rows look under the mouse and when selected. Items name their rows, so
/// they should differ from each other.
/// </summary>
public sealed record ListView<T>(IReadOnlyList<T> Items, Func<T, UiView> Row, T? Selected = null, Action<T>? OnSelect = null) : UiView
    where T : class
{
    private static readonly Color Clear = new(0f, 0f, 0f, 0f);
    private static readonly Color Hovered = new(1f, 1f, 1f, 0.07f);
    private static readonly Color Held = new(1f, 1f, 1f, 0.04f);
    private static readonly Color Chosen = new(0.36f, 0.52f, 0.78f, 0.45f);

    public override UiView Body
    {
        get
        {
            var scroll = new ScrollView();
            foreach (var item in Items)
            {
                scroll.Add(new Pressable(look => Describe(item, look), () => OnSelect?.Invoke(item)).Id(item));
            }

            return scroll;
        }
    }

    private UiView Describe(T item, PressState look)
    {
        var color = Equals(item, Selected) ? Chosen : look.Pressed ? Held : look.Hovered ? Hovered : Clear;
        return Row(item)
            .Frame(maxWidth: UiExtensions.Infinity, alignment: Alignment.Leading)
            .Padding(10f, 6f)
            .Background(color)
            .CornerRadius(4f);
    }
}

/// <summary>
/// Equal columns, filled left to right and then down. A row is as tall as its tallest
/// child, and a null child leaves its cell empty.
/// </summary>
public sealed record Grid(int Columns, float ColumnGap = 8f, float? RowGap = null) : Container
{
    private float Gaps => ColumnGap * (Columns - 1);

    internal override UiSize Measure(UiNode node, Proposal proposal)
    {
        float cell = CellWidth(node, proposal.Width);
        float height = 0f;
        foreach (float row in RowHeights(node, cell))
        {
            height += row + (RowGap ?? ColumnGap);
        }

        return new UiSize(cell * Columns + Gaps, Math.Max(height - (RowGap ?? ColumnGap), 0f));
    }

    internal override void Place(UiNode node, UiRect frame)
    {
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
                    child.Place(new UiRect(frame.X + column * (cell + ColumnGap), y, size.Width, size.Height));
                }
            }

            y += rows[row] + (RowGap ?? ColumnGap);
        }
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
