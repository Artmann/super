namespace Super.Ui;

public enum Axis
{
    Vertical,
    Horizontal,
}

/// <summary>
/// Shows content too long to fit, moved along one axis. Its children stack along that
/// axis. It fills the room it's offered, clips to it, and shows a scrollbar only when the
/// content overflows. The scrollbar takes its own lane, so it never covers the content.
/// </summary>
public sealed record ScrollView(Axis Axis = Axis.Vertical) : Container
{
    private static readonly Color TrackColor = new(1f, 1f, 1f, 0.05f);
    private static readonly Color ThumbColor = new(1f, 1f, 1f, 0.3f);
    private static readonly Color ThumbLitColor = new(1f, 1f, 1f, 0.5f);

    private bool Vertical => Axis == Axis.Vertical;

    internal override IReadOnlyList<UiView?> Children
    {
        get
        {
            Stack content = Vertical
                ? new VStack(HorizontalAlignment.Leading, 0f)
                : new HStack(VerticalAlignment.Top, 0f);
            foreach (var view in base.Children)
            {
                content.Add(view);
            }

            return [content];
        }
    }

    internal override UiSize Measure(UiNode node, Proposal proposal)
    {
        float? cross = Vertical ? proposal.Width : proposal.Height;
        float? main = Vertical ? proposal.Height : proposal.Width;
        var content = Measured(node, cross);
        float mainLength = main is { } offered && !float.IsInfinity(offered) ? offered : Axis.Main(content);
        if (main is { } limit && Axis.Main(content) > limit && cross is { } crossOffer)
        {
            content = Measured(node, Math.Max(crossOffer - UiScrollBar.Thickness, 0f));
            return Axis.SizeOf(mainLength, Math.Min(Axis.Cross(content) + UiScrollBar.Thickness, crossOffer));
        }

        return Axis.SizeOf(mainLength, cross is { } most ? Math.Min(Axis.Cross(content), most) : Axis.Cross(content));
    }

    internal override void Place(UiNode node, UiRect frame)
    {
        float viewport = Vertical ? frame.Height : frame.Width;
        float cross = Vertical ? frame.Width : frame.Height;
        var content = Measured(node, cross);
        bool overflows = Axis.Main(content) > viewport;
        if (overflows)
        {
            content = Measured(node, Math.Max(cross - UiScrollBar.Thickness, 0f));
        }

        var scroll = node.Scroll;
        scroll.ContentLength = Axis.Main(content);
        scroll.ViewportLength = viewport;
        scroll.Offset = Math.Clamp(scroll.Offset, 0f, scroll.MaxScroll);
        var inner = node.Children[0]!;
        inner.Place(Vertical
            ? new UiRect(frame.X, frame.Y - scroll.Offset, content.Width, content.Height)
            : new UiRect(frame.X - scroll.Offset, frame.Y, content.Width, content.Height));
    }

    internal override bool Blocks => true;

    internal override UiRect? ClipArea(UiNode node) => node.Frame;

    internal override bool HitsFirst(UiNode node, float x, float y) => BarOf(node).Contains(x, y);

    internal override void Emit(UiNode node, UiDrawList draw) => draw.Nesting.PushClip(node.Frame, 0f);

    /// <summary>The bar comes after the clip ends, so it can't be cut off by what it scrolls.</summary>
    internal override void EmitAfter(UiNode node, UiDrawList draw)
    {
        draw.Nesting.PopClip();
        if (node.Scroll.MaxScroll > 0f)
        {
            float radius = UiScrollBar.Thickness / 2f;
            var bar = BarOf(node);
            draw.AddBox(bar.Track, TrackColor, radius, 0f, default);
            draw.AddBox(bar.Thumb, node.Scroll.BarLit ? ThumbLitColor : ThumbColor, radius, 0f, default);
        }
    }

    /// <summary>This view's bar, where its node was laid out last.</summary>
    internal UiScrollBar BarOf(UiNode node) => new(node.Frame, Vertical, node.Scroll);

    /// <summary>The content's size, offered this much room across the axis and as much as it likes along it.</summary>
    private UiSize Measured(UiNode node, float? cross) =>
        node.Children[0]!.Measure(Vertical ? new Proposal(Finite(cross), null) : new Proposal(null, Finite(cross)));

    private static float? Finite(float? length) => length is { } value && float.IsInfinity(value) ? null : length;
}
