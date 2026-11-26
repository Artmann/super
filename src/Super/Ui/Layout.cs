namespace Super.Ui;

/// <summary>How <see cref="LayoutExtensions.AspectRatio"/> fits its shape to the room: all of it inside, or covering all of the room.</summary>
public enum ContentMode
{
    /// <summary>As big as fits inside the room.</summary>
    Fit,

    /// <summary>As small as covers the room, so part of it may stick out.</summary>
    Fill,
}

/// <summary>Layout modifiers in the way of SwiftUI: views over and under a view, moving one, hiding one, and its share of a stack.</summary>
public static class LayoutExtensions
{
    /// <summary>
    /// Draws <paramref name="over"/> in front of this view, inside its frame at this alignment. The view alone sets the size,
    /// so a badge on a corner or a border round a card doesn't move anything.
    /// </summary>
    public static UiView Overlay(this UiView view, UiView over, Alignment alignment = Alignment.Center) =>
        new LayeredModifier(view, over, alignment, Behind: false);

    /// <summary>Draws <paramref name="under"/> behind this view, inside its frame at this alignment. The view alone sets the size.</summary>
    public static UiView Background(this UiView view, UiView under, Alignment alignment = Alignment.Center) =>
        new LayeredModifier(view, under, alignment, Behind: true);

    /// <summary>Draws this view and takes the pointer this far from where it's laid out, without moving anything round it.</summary>
    public static UiView Offset(this UiView view, float x = 0f, float y = 0f) => new OffsetModifier(view, x, y);

    /// <summary>
    /// Keeps this view's room in the layout but shows nothing there: it draws nothing, takes no pointer, and holds no
    /// focus or control. To take the room away too, leave the view out with null.
    /// </summary>
    public static UiView Hidden(this UiView view, bool hidden = true) => hidden ? new HiddenModifier(view) : view;

    /// <summary>
    /// How soon a stack serves this view. A stack offers room to its higher priorities first, keeping back only what the
    /// lower ones can't do without, so the label you'd rather not cut gets its line. The default is 0.
    /// </summary>
    public static UiView LayoutPriority(this UiView view, float priority) => new LayoutPriorityModifier(view, priority);

    /// <summary>
    /// Offers this view a size of this width over height: the biggest that fits the room, or with <see cref="ContentMode.Fill"/>
    /// the smallest that covers it. Shapes and images take it; text takes what it needs of it.
    /// </summary>
    public static UiView AspectRatio(this UiView view, float ratio, ContentMode mode = ContentMode.Fit)
    {
        if (!(ratio > 0f) || !float.IsFinite(ratio))
        {
            throw new ArgumentOutOfRangeException(nameof(ratio), ratio, "An aspect ratio is a width over a height, above zero.");
        }

        return new AspectRatioModifier(view, ratio, mode);
    }
}

/// <summary>A view with another drawn in front of it or behind it, which takes its frame but not its size.</summary>
internal sealed record LayeredModifier(UiView Child, UiView Extra, Alignment Alignment, bool Behind) : PrimitiveView
{
    internal override IReadOnlyList<UiView?> Children => Behind ? [Extra, Child] : [Child, Extra];

    private UiNode Main(UiNode node) => node.Children[Behind ? 1 : 0]!;

    private UiNode Added(UiNode node) => node.Children[Behind ? 0 : 1]!;

    internal override UiSize Measure(UiNode node, Proposal proposal) => Main(node).Measure(proposal);

    internal override void Place(UiNode node, UiRect frame)
    {
        Main(node).Place(frame);
        var added = Added(node);
        added.Place(frame.Align(added.Measure(new Proposal(frame.Width, frame.Height)), Alignment));
    }
}

/// <summary>Moves a view where it's drawn and hit, not where it's laid out.</summary>
internal sealed record OffsetModifier(UiView Child, float X, float Y) : Modifier(Child)
{
    internal override void Place(UiNode node, UiRect frame) => Inner(node).Place(frame with { X = frame.X + X, Y = frame.Y + Y });
}

/// <summary>Lays a view out and shows none of it.</summary>
internal sealed record HiddenModifier(UiView Child) : Modifier(Child)
{
    internal override bool Shows(UiNode node, int child) => false;
}

/// <summary>A view's place in the queue for a stack's room.</summary>
internal sealed record LayoutPriorityModifier(UiView Child, float Priority) : Modifier(Child)
{
    /// <summary>The priority of the view at this node, looking through the modifiers round it.</summary>
    public static float Of(UiNode node)
    {
        for (UiNode? at = node; at is not null; at = at.Children.Count == 1 ? at.Children[0] : null)
        {
            if (at.View is LayoutPriorityModifier { Priority: var priority })
            {
                return priority;
            }

            if (at.View is PrimitiveView and not Modifier)
            {
                return 0f;
            }
        }

        return 0f;
    }
}

/// <summary>Offers a view a size of one shape, fitted to the room or covering it.</summary>
internal sealed record AspectRatioModifier(UiView Child, float Ratio, ContentMode Mode) : Modifier(Child)
{
    internal override UiSize Measure(UiNode node, Proposal proposal) => Inner(node).Measure(Offer(node, proposal));

    internal override void Place(UiNode node, UiRect frame)
    {
        var inner = Inner(node);
        inner.Place(frame.Align(inner.Measure(Offer(node, new Proposal(frame.Width, frame.Height))), Alignment.Center));
    }

    private Proposal Offer(UiNode node, Proposal proposal)
    {
        float? width = Finite(proposal.Width);
        float? height = Finite(proposal.Height);
        if (width is { } w && height is { } h)
        {
            bool wider = w / h > Ratio;
            return wider == (Mode == ContentMode.Fit) ? new Proposal(h * Ratio, h) : new Proposal(w, w / Ratio);
        }

        if (width is { } across)
        {
            return new Proposal(across, across / Ratio);
        }

        if (height is { } down)
        {
            return new Proposal(down * Ratio, down);
        }

        float ideal = Inner(node).Measure(new Proposal(null, null)).Width;
        return new Proposal(ideal, ideal / Ratio);
    }

    private static float? Finite(float? length) => length is { } value && float.IsFinite(value) ? value : null;
}

/// <summary>
/// Lays its children out in a row and starts a new row when the next one doesn't fit, like words in a paragraph:
/// tags, chips, a hand of cards. Each child is its ideal size, cut to the width when it's wider than that.
/// </summary>
public sealed record FlowStack(float Spacing = 8f, float LineSpacing = 8f, HorizontalAlignment Alignment = HorizontalAlignment.Leading) : Container
{
    internal override UiSize Measure(UiNode node, Proposal proposal)
    {
        var flow = new UiFlow(node, proposal.Width, Spacing);
        float height = 0f;
        float width = 0f;
        foreach (var line in flow.Lines)
        {
            height += line.Height;
            width = Math.Max(width, line.Width);
        }

        return new UiSize(width, height + LineSpacing * Math.Max(flow.Lines.Count - 1, 0));
    }

    internal override void Place(UiNode node, UiRect frame)
    {
        var flow = new UiFlow(node, frame.Width, Spacing);
        float y = frame.Y;
        foreach (var line in flow.Lines)
        {
            float x = frame.X + (frame.Width - line.Width) * Alignment.Fraction();
            for (int i = line.Start; i < line.End; i++)
            {
                var size = flow.Sizes[i];
                flow.Children[i].Place(new UiRect(x, y + (line.Height - size.Height) / 2f, size.Width, size.Height));
                x += size.Width + Spacing;
            }

            y += line.Height + LineSpacing;
        }
    }
}

/// <summary>A flow stack's children broken into lines for one width.</summary>
internal sealed class UiFlow
{
    public UiFlow(UiNode node, float? width, float spacing)
    {
        Children = node.Children.OfType<UiNode>().ToArray();
        Sizes = new UiSize[Children.Length];
        float limit = width is { } offered && float.IsFinite(offered) ? offered : float.PositiveInfinity;
        int start = 0;
        float x = 0f;
        float height = 0f;
        for (int i = 0; i < Children.Length; i++)
        {
            var size = Children[i].Measure(new Proposal(null, null));
            if (size.Width > limit)
            {
                size = Children[i].Measure(new Proposal(limit, null));
            }

            Sizes[i] = size;
            if (i > start && x + spacing + size.Width > limit)
            {
                Lines.Add(new UiFlowLine(start, i, x, height));
                (start, x, height) = (i, 0f, 0f);
            }

            x += (i > start ? spacing : 0f) + size.Width;
            height = Math.Max(height, size.Height);
        }

        if (Children.Length > start)
        {
            Lines.Add(new UiFlowLine(start, Children.Length, x, height));
        }
    }

    public UiNode[] Children { get; }

    public UiSize[] Sizes { get; }

    public List<UiFlowLine> Lines { get; } = [];
}

/// <summary>One line of a flow: the children from Start up to End, and how wide and tall they are together.</summary>
internal readonly record struct UiFlowLine(int Start, int End, float Width, float Height);

/// <summary>
/// Shows the first of its children that fits the room at its ideal size, or the last when none does: a full label,
/// then a short one, then only an icon. Only the one shown draws, takes the pointer and holds focus.
/// Give <paramref name="In"/> to only count one direction, such as a row that may wrap onto more lines.
/// </summary>
public sealed record ViewThatFits(Axis? In = null) : Container
{
    // How far a size may go over and still count as fitting.
    private const float Tolerance = 0.5f;

    internal override UiSize Measure(UiNode node, Proposal proposal) =>
        Chosen(node, proposal) is { } chosen ? node.Children[chosen]!.Measure(proposal) : default;

    internal override void Place(UiNode node, UiRect frame)
    {
        if (Chosen(node, new Proposal(frame.Width, frame.Height)) is { } chosen)
        {
            var child = node.Children[chosen]!;
            child.Place(frame.Align(child.Measure(new Proposal(frame.Width, frame.Height)), Alignment.Center));
        }
    }

    internal override bool Shows(UiNode node, int child) => child == Chosen(node, new Proposal(node.Frame.Width, node.Frame.Height));

    private int? Chosen(UiNode node, Proposal proposal)
    {
        int? last = null;
        for (int i = 0; i < node.Children.Count; i++)
        {
            if (node.Children[i] is not { } child)
            {
                continue;
            }

            last = i;
            var ideal = child.Measure(new Proposal(In == Axis.Vertical ? proposal.Width : null, In == Axis.Horizontal ? proposal.Height : null));
            if ((In == Axis.Vertical || Fits(ideal.Width, proposal.Width)) && (In == Axis.Horizontal || Fits(ideal.Height, proposal.Height)))
            {
                return i;
            }
        }

        return last;
    }

    private static bool Fits(float length, float? room) => room is not { } most || length <= most + Tolerance;
}
