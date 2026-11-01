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
    internal const float BarThickness = 10f;
    internal const float MinThumb = 28f;

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
        var content = Content(node, cross);
        float mainLength = main is { } offered && !float.IsInfinity(offered) ? offered : Main(content);
        if (main is { } limit && Main(content) > limit && cross is { } crossOffer)
        {
            content = Content(node, Math.Max(crossOffer - BarThickness, 0f));
            return Size(mainLength, Math.Min(Cross(content) + BarThickness, crossOffer));
        }

        return Size(mainLength, cross is { } most ? Math.Min(Cross(content), most) : Cross(content));
    }

    internal override void Place(UiNode node, UiRect frame)
    {
        float viewport = Vertical ? frame.Height : frame.Width;
        float cross = Vertical ? frame.Width : frame.Height;
        var content = Content(node, cross);
        bool overflows = Main(content) > viewport;
        if (overflows)
        {
            content = Content(node, Math.Max(cross - BarThickness, 0f));
        }

        node.ContentLength = Main(content);
        node.ViewportLength = viewport;
        node.ScrollOffset = Math.Clamp(node.ScrollOffset, 0f, node.MaxScroll);
        var inner = node.Children[0]!;
        inner.Place(Vertical
            ? new UiRect(frame.X, frame.Y - node.ScrollOffset, content.Width, content.Height)
            : new UiRect(frame.X - node.ScrollOffset, frame.Y, content.Width, content.Height));
    }

    internal override void Emit(UiNode node, UiTree tree) => tree.PushClip(node.Frame, 0f);

    internal override void EmitAfter(UiNode node, UiTree tree) => tree.PopClip();

    private UiSize Content(UiNode node, float? cross) =>
        node.Children[0]!.Measure(Vertical ? new Proposal(Finite(cross), null) : new Proposal(null, Finite(cross)));

    private static float? Finite(float? length) => length is { } value && float.IsInfinity(value) ? null : length;

    private float Main(UiSize size) => Vertical ? size.Height : size.Width;

    private float Cross(UiSize size) => Vertical ? size.Width : size.Height;

    private UiSize Size(float main, float cross) => Vertical ? new UiSize(cross, main) : new UiSize(main, cross);
}
