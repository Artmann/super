namespace Super.Ui;

/// <summary>
/// A column of rows that only makes the rows near the screen, for lists too long to make whole: a leaderboard, a
/// server browser, a log. Put it in a <see cref="ScrollView"/>; outside one it makes every row. Every row is <paramref name="RowHeight"/> tall, so the
/// column knows its length without making them, and it keeps a screen's worth of rows made above and below what shows,
/// so scrolling and moving focus find them ready. Items name their rows, so they should differ from each other. Hand
/// it a new list when the items change: a list changed in place looks the same as the last one.
/// </summary>
public sealed record LazyVStack<T>(IReadOnlyList<T> Items, Func<T, UiView> Row, float RowHeight, float Spacing = 0f) : UiView
{
    // How many rows to make before the first layout says which show.
    private const int FirstMade = 32;

    public override UiView Body
    {
        get
        {
            if (!(RowHeight > 0f) || !float.IsFinite(RowHeight))
            {
                throw new ArgumentOutOfRangeException(nameof(RowHeight), RowHeight, "A lazy stack's rows need a height above zero.");
            }

            var made = UseState(new UiLazyWindow(0, Math.Min(Items.Count, FirstMade)));
            var window = made.Value.Within(Items.Count);
            var rows = new UiLazyRows(Items.Count, RowHeight, Spacing, window.First, made);
            for (int i = window.First; i < window.First + window.Count; i++)
            {
                var item = Items[i];
                rows.Add(Row(item).Frame(height: RowHeight, maxWidth: UiExtensions.Infinity, alignment: Alignment.Leading).Id((object?)item ?? i));
            }

            return rows;
        }
    }
}

/// <summary>The rows a lazy stack has made: from First, this many.</summary>
internal readonly record struct UiLazyWindow(int First, int Count)
{
    /// <summary>This window cut to a list of this many rows.</summary>
    public UiLazyWindow Within(int total)
    {
        int first = Math.Clamp(First, 0, total);
        return new UiLazyWindow(first, Math.Clamp(Count, 0, total - first));
    }
}

/// <summary>
/// The made rows of a lazy stack, each in its place in the whole column. Once placed, it works out which rows the
/// scroll view round it shows, and when some of those aren't made, it asks for them, a screen either side.
/// </summary>
internal sealed record UiLazyRows(int Total, float RowHeight, float Spacing, int First, State<UiLazyWindow> Made) : Container
{
    private float Stride => RowHeight + Spacing;

    internal override UiSize Measure(UiNode node, Proposal proposal)
    {
        float width = 0f;
        foreach (var child in node.Children.OfType<UiNode>())
        {
            width = Math.Max(width, child.Measure(new Proposal(proposal.Width, RowHeight)).Width);
        }

        return new UiSize(width, Math.Max(Total * Stride - Spacing, 0f));
    }

    internal override void Place(UiNode node, UiRect frame)
    {
        for (int i = 0; i < node.Children.Count; i++)
        {
            node.Children[i]?.Place(new UiRect(frame.X, frame.Y + (First + i) * Stride, frame.Width, RowHeight));
        }

        if (UiPointerSearch.Find<ScrollView>(node.Parent) is { } scroll)
        {
            Follow(node, frame, scroll.Frame);
        }
        else if (node.Children.Count < Total)
        {
            // With nothing to scroll it, the whole column shows.
            Made.Value = new UiLazyWindow(0, Total);
        }
    }

    private void Follow(UiNode node, UiRect frame, UiRect shown)
    {
        float top = shown.Y - frame.Y;
        float bottom = top + shown.Height;
        int firstShown = Math.Clamp((int)MathF.Floor(top / Stride), 0, Total);
        int endShown = Math.Clamp((int)MathF.Ceiling(bottom / Stride), firstShown, Total);
        int made = node.Children.Count;
        if (firstShown >= First && endShown <= First + made)
        {
            return;
        }

        // A screen's worth either side, so the next scroll or focus move finds its rows made.
        int screen = Math.Max(endShown - firstShown, 1);
        int first = Math.Max(firstShown - screen, 0);
        Made.Value = new UiLazyWindow(first, Math.Min(endShown + screen, Total) - first);
    }
}
