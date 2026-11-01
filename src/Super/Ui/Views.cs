using System.Collections;

namespace Super.Ui;

/// <summary>A line or paragraph of text. It wraps at spaces when it's offered too little width.</summary>
public sealed record Text(string Content) : PrimitiveView
{
    internal override IReadOnlyList<UiView?> Children => [];

    internal override UiSize Measure(UiNode node, Proposal proposal) =>
        node.Tree.Atlas.Measure(Content, node.Style, proposal.Width);

    internal override void Place(UiNode node, UiRect frame)
    {
    }

    internal override void Emit(UiNode node, UiTree tree)
    {
        tree.AddText(Content, node.Frame);
        tree.Atlas.Draw(tree, Content, node.Style, node.Frame);
    }
}

/// <summary>Pushes its neighbors apart, along the direction of the stack it's in.</summary>
public sealed record Spacer(float MinLength = 0f) : PrimitiveView
{
    internal override IReadOnlyList<UiView?> Children => [];

    internal override UiSize Measure(UiNode node, Proposal proposal)
    {
        for (var parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent.View is Stack stack)
            {
                float length = Math.Max(stack.MainOf(proposal) ?? 0f, MinLength);
                return stack.ToSize(length, 0f);
            }
        }

        return default;
    }

    internal override void Place(UiNode node, UiRect frame)
    {
    }
}

/// <summary>A view with a list of children, written with collection syntax.</summary>
public abstract record Container : PrimitiveView, IEnumerable<UiView?>
{
    private readonly List<UiView?> _items = [];

    internal override IReadOnlyList<UiView?> Children => _items;

    /// <summary>Adds a child. Null keeps the spot empty, so `show ? view : null` doesn't disturb what follows.</summary>
    public void Add(UiView? view) => _items.Add(view);

    /// <summary>Adds a stack inside this one. It's one child, even though it's also a list.</summary>
    public void Add(Container view) => _items.Add(view);

    /// <summary>Adds several children, such as a list of rows. Give them an Id if the list changes.</summary>
    public void Add(IEnumerable<UiView> views) => _items.AddRange(views);

    public IEnumerator<UiView?> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    /// <summary>Two containers match when their children do, which lets an unchanged stack be skipped.</summary>
    public virtual bool Equals(Container? other) =>
        base.Equals(other) && _items.SequenceEqual(other!._items);

    public override int GetHashCode() => HashCode.Combine(base.GetHashCode(), _items.Count);
}

/// <summary>Stacks its children along one direction, in the way of SwiftUI and flexbox.</summary>
public abstract record Stack(float Spacing) : Container
{
    private protected abstract bool IsHorizontal { get; }

    private protected abstract float CrossFraction { get; }

    internal float? MainOf(Proposal proposal) => IsHorizontal ? proposal.Width : proposal.Height;

    internal UiSize ToSize(float main, float cross) => IsHorizontal ? new UiSize(main, cross) : new UiSize(cross, main);

    internal override UiSize Measure(UiNode node, Proposal proposal)
    {
        float main = 0f;
        float cross = 0f;
        var sizes = Share(node, proposal);
        foreach (var size in sizes)
        {
            main += Main(size);
            cross = Math.Max(cross, Cross(size));
        }

        return ToSize(main + TotalSpacing(sizes.Length), cross);
    }

    internal override void Place(UiNode node, UiRect frame)
    {
        var children = VisibleChildren(node);
        var sizes = Share(node, new Proposal(frame.Width, frame.Height));
        float start = IsHorizontal ? frame.X : frame.Y;
        float crossStart = IsHorizontal ? frame.Y : frame.X;
        float crossLength = IsHorizontal ? frame.Height : frame.Width;
        for (int i = 0; i < children.Length; i++)
        {
            float crossOffset = crossStart + (crossLength - Cross(sizes[i])) * CrossFraction;
            var position = ToSize(start, crossOffset);
            children[i].Place(new UiRect(position.Width, position.Height, sizes[i].Width, sizes[i].Height));
            start += Main(sizes[i]) + Spacing;
        }
    }

    private static UiNode[] VisibleChildren(UiNode node) => node.Children.OfType<UiNode>().ToArray();

    private float Main(UiSize size) => IsHorizontal ? size.Width : size.Height;

    private float Cross(UiSize size) => IsHorizontal ? size.Height : size.Width;

    private float? CrossOf(Proposal proposal) => IsHorizontal ? proposal.Height : proposal.Width;

    private float TotalSpacing(int count) => Spacing * Math.Max(count - 1, 0);

    private Proposal Propose(float? main, float? cross) =>
        IsHorizontal ? new Proposal(main, cross) : new Proposal(cross, main);

    /// <summary>
    /// Splits the space, least flexible child first. Each is offered an equal share of
    /// what's left, so anything it doesn't use goes to the ones that can stretch.
    /// </summary>
    private UiSize[] Share(UiNode node, Proposal proposal)
    {
        var children = VisibleChildren(node);
        var sizes = new UiSize[children.Length];
        float? cross = CrossOf(proposal);
        if (MainOf(proposal) is not { } offered)
        {
            for (int i = 0; i < children.Length; i++)
            {
                sizes[i] = children[i].Measure(Propose(null, cross));
            }

            return sizes;
        }

        var flexibility = new float[children.Length];
        for (int i = 0; i < children.Length; i++)
        {
            float smallest = Main(children[i].Measure(Propose(0f, cross)));
            float largest = Main(children[i].Measure(Propose(float.PositiveInfinity, cross)));
            flexibility[i] = largest - smallest;
        }

        // Spacers wait until everything else is served, holding on to only their minimum.
        // Otherwise a spacer would take half the room from text that needs it.
        var order = Enumerable.Range(0, children.Length).OrderBy(index => flexibility[index]).ToArray();
        int[] spacers = order.Where(index => children[index].View is Spacer).ToArray();
        int[] others = order.Where(index => children[index].View is not Spacer).ToArray();
        float held = spacers.Sum(index => Main(children[index].Measure(Propose(0f, cross))));
        float available = offered - TotalSpacing(children.Length);
        float left = Allot(children, sizes, others, available - held, cross);
        Allot(children, sizes, spacers, left + held, cross);
        return sizes;
    }

    /// <summary>Offers each child an equal share of what's left, and returns what nobody took.</summary>
    private float Allot(UiNode[] children, UiSize[] sizes, int[] order, float remaining, float? cross)
    {
        remaining = Math.Max(remaining, 0f);
        for (int i = 0; i < order.Length; i++)
        {
            int child = order[i];
            sizes[child] = children[child].Measure(Propose(remaining / (order.Length - i), cross));
            if (!float.IsInfinity(remaining))
            {
                remaining = Math.Max(remaining - Main(sizes[child]), 0f);
            }
        }

        return remaining;
    }
}

/// <summary>Stacks its children from top to bottom.</summary>
public sealed record VStack(HorizontalAlignment Alignment = HorizontalAlignment.Center, float Spacing = 8f) : Stack(Spacing)
{
    private protected override bool IsHorizontal => false;

    private protected override float CrossFraction => Alignment.Fraction();
}

/// <summary>Stacks its children from left to right.</summary>
public sealed record HStack(VerticalAlignment Alignment = VerticalAlignment.Center, float Spacing = 8f) : Stack(Spacing)
{
    private protected override bool IsHorizontal => true;

    private protected override float CrossFraction => Alignment.Fraction();
}

/// <summary>Stacks its children on top of each other, the last one in front.</summary>
public sealed record ZStack(Alignment Alignment = Alignment.Center) : Container
{
    internal override UiSize Measure(UiNode node, Proposal proposal)
    {
        float width = 0f;
        float height = 0f;
        foreach (var child in node.Children.OfType<UiNode>())
        {
            var size = child.Measure(proposal);
            width = Math.Max(width, size.Width);
            height = Math.Max(height, size.Height);
        }

        return new UiSize(width, height);
    }

    internal override void Place(UiNode node, UiRect frame)
    {
        foreach (var child in node.Children.OfType<UiNode>())
        {
            child.Place(frame.Align(child.Measure(new Proposal(frame.Width, frame.Height)), Alignment));
        }
    }
}

/// <summary>Wraps one view. By default it's invisible to layout.</summary>
internal abstract record Modifier(UiView Child) : PrimitiveView
{
    internal override IReadOnlyList<UiView?> Children => [Child];

    protected static UiNode Inner(UiNode node) => node.Children[0]!;

    internal override UiSize Measure(UiNode node, Proposal proposal) => Inner(node).Measure(proposal);

    internal override void Place(UiNode node, UiRect frame) => Inner(node).Place(frame);
}

internal sealed record PaddingModifier(UiView Child, Edges Edges) : Modifier(Child)
{
    internal override UiSize Measure(UiNode node, Proposal proposal) =>
        Edges.Grow(Inner(node).Measure(Edges.Shrink(proposal)));

    internal override void Place(UiNode node, UiRect frame) => Inner(node).Place(Edges.Shrink(frame));
}

internal sealed record FrameModifier(
    UiView Child,
    float? Width,
    float? Height,
    float? MaxWidth,
    float? MaxHeight,
    Alignment Alignment,
    float? MinWidth = null,
    float? MinHeight = null) : Modifier(Child)
{
    internal override UiSize Measure(UiNode node, Proposal proposal)
    {
        var inner = Inner(node).Measure(Offer(proposal));
        return new UiSize(
            Resolve(Width, MinWidth, MaxWidth, proposal.Width, inner.Width),
            Resolve(Height, MinHeight, MaxHeight, proposal.Height, inner.Height));
    }

    internal override void Place(UiNode node, UiRect frame)
    {
        var inner = Inner(node);
        inner.Place(frame.Align(inner.Measure(Offer(new Proposal(frame.Width, frame.Height))), Alignment));
    }

    private static float? Limit(float? offered, float? fixedLength, float? min, float? max)
    {
        if (fixedLength is not null)
        {
            return fixedLength;
        }

        if (offered is not { } value)
        {
            return null;
        }

        value = max is { } limit ? Math.Min(value, limit) : value;
        return min is { } least ? Math.Max(value, least) : value;
    }

    private static float Resolve(float? fixedLength, float? min, float? max, float? offered, float inner)
    {
        if (fixedLength is { } length)
        {
            return length;
        }

        // A maximum stretches to what's offered. With nothing offered, it's only a limit.
        float size = inner;
        if (max is { } limit)
        {
            size = offered is { } value ? Math.Min(value, limit) : Math.Min(inner, limit);
        }

        return min is { } least ? Math.Max(size, least) : size;
    }

    private Proposal Offer(Proposal proposal) =>
        new(Limit(proposal.Width, Width, MinWidth, MaxWidth), Limit(proposal.Height, Height, MinHeight, MaxHeight));
}

/// <summary>Sizes the view as it would like to be, whatever it's offered.</summary>
internal sealed record FixedSizeModifier(UiView Child) : Modifier(Child)
{
    internal override UiSize Measure(UiNode node, Proposal proposal) => Inner(node).Measure(new Proposal(null, null));
}

/// <summary>
/// One CSS box. Neighbouring background, border, corner, opacity and margin modifiers
/// all land in the same one when they're written, which is why their order doesn't matter.
/// </summary>
internal sealed record BoxModifier(UiView Child) : Modifier(Child)
{
    public Edges Margin { get; init; }

    public Color? Fill { get; init; }

    public float BorderWidth { get; init; }

    public Color BorderColor { get; init; }

    public float Radius { get; init; }

    public float Alpha { get; init; } = 1f;

    /// <summary>The space inside the margin, which is what's drawn and what the mouse can hit.</summary>
    public UiRect BoxOf(UiNode node) => Margin.Shrink(node.Frame);

    internal override UiSize Measure(UiNode node, Proposal proposal) =>
        Margin.Grow(Inner(node).Measure(Margin.Shrink(proposal)));

    internal override void Place(UiNode node, UiRect frame) => Inner(node).Place(Margin.Shrink(frame));

    private bool Clips => Radius > 0f;

    internal override bool Blocks => Fill is not null || BorderWidth > 0f;

    internal override UiRect HitArea(UiNode node) => BoxOf(node);

    internal override UiRect? ClipArea(UiNode node) => Clips ? BoxOf(node) : null;

    internal override void Emit(UiNode node, UiTree tree)
    {
        tree.PushOpacity(Alpha);
        var box = BoxOf(node);
        if (Fill is not null || BorderWidth > 0f)
        {
            tree.AddBox(box, Fill, Radius, BorderWidth, BorderColor);
        }

        if (Clips)
        {
            tree.PushClip(box, Radius);
        }
    }

    internal override void EmitAfter(UiNode node, UiTree tree)
    {
        if (Clips)
        {
            tree.PopClip();
        }

        tree.PopOpacity();
    }
}

internal sealed record StyleModifier(UiView Child, float? Size = null, bool? Bold = null, Color? Color = null, bool? MonospacedDigits = null)
    : Modifier(Child)
{
    internal override TextStyle Inherit(TextStyle style) => new(
        Size ?? style.Size, Bold ?? style.Bold, Color ?? style.Color, MonospacedDigits ?? style.MonospacedDigits);
}
