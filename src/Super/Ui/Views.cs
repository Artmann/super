using System.Collections;

namespace Super.Ui;

/// <summary>
/// A line or paragraph of text. It wraps at spaces when it's offered too little width, unless
/// a <see cref="TextExtensions.LineLimit"/> cuts it short with an ellipsis.
/// </summary>
public sealed record Text(string Content) : PrimitiveView
{
    internal override IReadOnlyList<UiView?> Children => [];

    internal override UiSize Measure(UiNode node, Proposal proposal) =>
        node.Tree.Atlas.Measure(Content, node.Environment.Text, proposal.Width);

    internal override void Place(UiNode node, UiRect frame)
    {
    }

    internal override void Emit(UiNode node, UiDrawList draw)
    {
        var atlas = node.Tree.Atlas;
        draw.AddText(atlas.Shown(Content, node.Environment.Text, node.Frame.Width), node.Frame, node.Environment.Text);
        atlas.Draw(draw, Content, node.Environment.Text, node.Frame);
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
    /// <summary>How far sums of lengths may drift and still count as fitting.</summary>
    private const float WidthTolerance = 0.01f;

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

    internal float? CrossOf(Proposal proposal) => IsHorizontal ? proposal.Height : proposal.Width;

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

        var line = Lengths(children, sizes, cross);

        // Spacers wait until everything else is served, holding on to only their minimum.
        // Otherwise a spacer would take half the room from text that needs it.
        var order = Enumerable.Range(0, children.Length).OrderBy(index => line.Flexibility[index]).ToArray();
        int[] spacers = order.Where(index => children[index].View is Spacer).ToArray();
        int[] others = order.Where(index => children[index].View is not Spacer).ToArray();
        float held = spacers.Sum(index => line.Smallest[index]);
        float available = offered - TotalSpacing(children.Length);
        float left = AllotByPriority(line, others, available - held);
        Allot(line, spacers, left + held);
        return sizes;
    }

    /// <summary>How long each child is at its smallest and ideal, how far it stretches, and its layout priority.</summary>
    private StackLine Lengths(UiNode[] children, UiSize[] sizes, float? cross)
    {
        var line = new StackLine(children, sizes, new float[children.Length], cross)
        {
            Smallest = new float[children.Length],
            Flexibility = new float[children.Length],
            Priority = new float[children.Length],
        };
        for (int i = 0; i < children.Length; i++)
        {
            line.Smallest[i] = Main(children[i].Measure(Propose(0f, cross)));
            line.Flexibility[i] = Main(children[i].Measure(Propose(float.PositiveInfinity, cross))) - line.Smallest[i];
            line.Ideal[i] = Main(children[i].Measure(Propose(null, cross)));
            line.Priority[i] = LayoutPriorityModifier.Of(children[i]);
        }

        return line;
    }

    /// <summary>
    /// Serves the highest priority first. Each is offered what's left once the lower priorities' smallest
    /// lengths are kept back for them, so they shrink first. Returns what nobody took.
    /// </summary>
    private float AllotByPriority(StackLine line, int[] order, float remaining)
    {
        foreach (float priority in order.Select(index => line.Priority[index]).Distinct().OrderDescending())
        {
            int[] group = order.Where(index => line.Priority[index] == priority).ToArray();
            float reserved = order.Where(index => line.Priority[index] < priority).Sum(index => line.Smallest[index]);
            remaining = Allot(line, group, remaining - reserved) + reserved;
        }

        return remaining;
    }

    /// <summary>
    /// Offers each child an equal share of what's left, and returns what nobody took. While everything left
    /// still fits at its ideal length, a child is offered at least that: a stack placed at the length it
    /// asked for then lays out as it measured, rather than halving it for text that would wrap.
    /// </summary>
    private float Allot(StackLine line, int[] order, float remaining)
    {
        remaining = Math.Max(remaining, 0f);
        float wanted = order.Sum(index => line.Ideal[index]);
        for (int i = 0; i < order.Length; i++)
        {
            int child = order[i];
            float share = remaining / (order.Length - i);
            float offer = wanted <= remaining + WidthTolerance ? Math.Max(share, line.Ideal[child]) : share;
            wanted -= line.Ideal[child];
            line.Sizes[child] = line.Children[child].Measure(Propose(offer, line.Cross));
            if (!float.IsInfinity(remaining))
            {
                remaining = Math.Max(remaining - Main(line.Sizes[child]), 0f);
            }
        }

        return remaining;
    }
}

/// <summary>A stack's children as it shares out its length: their sizes so far, each one's ideal length, and the cross length offered.</summary>
internal readonly record struct StackLine(UiNode[] Children, UiSize[] Sizes, float[] Ideal, float? Cross)
{
    public float[] Smallest { get; init; } = [];

    public float[] Flexibility { get; init; } = [];

    public float[] Priority { get; init; } = [];
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
        var offer = Offer(proposal);
        var inner = Inner(node).Measure(offer);

        // With nothing offered, what's over a maximum has to fit it, and may wrap and get taller.
        var limited = new Proposal(Limited(offer.Width, inner.Width, MaxWidth), Limited(offer.Height, inner.Height, MaxHeight));
        if (limited != offer)
        {
            inner = Inner(node).Measure(limited);
        }

        return new UiSize(
            Resolve(Width, MinWidth, MaxWidth, proposal.Width, inner.Width),
            Resolve(Height, MinHeight, MaxHeight, proposal.Height, inner.Height));
    }

    internal override void Place(UiNode node, UiRect frame)
    {
        var inner = Inner(node);
        inner.Place(frame.Align(inner.Measure(Offer(new Proposal(frame.Width, frame.Height))), Alignment));
    }

    private static float? Limited(float? offered, float wanted, float? max) =>
        offered is null && wanted > max ? max : offered;

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

    public Paint? Fill { get; init; }

    public float BorderWidth { get; init; }

    public Color BorderColor { get; init; }

    public CornerRadii Radius { get; init; }

    public Shadow? Shadow { get; init; }

    /// <summary>One of the theme's shadows, looked up where it's drawn.</summary>
    public ShadowSize? ThemeShadow { get; init; }

    public float Alpha { get; init; } = 1f;

    /// <summary>The space inside the margin, which is what's drawn and what the mouse can hit.</summary>
    public UiRect BoxOf(UiNode node) => Margin.Shrink(node.Frame);

    internal override UiSize Measure(UiNode node, Proposal proposal) =>
        Margin.Grow(Inner(node).Measure(Margin.Shrink(proposal)));

    internal override void Place(UiNode node, UiRect frame) => Inner(node).Place(Margin.Shrink(frame));

    private bool Clips => Radius.Any;

    internal override bool Blocks => Fill is not null || BorderWidth > 0f;

    internal override UiRect HitArea(UiNode node) => BoxOf(node);

    internal override UiRect? ClipArea(UiNode node) => Clips ? BoxOf(node) : null;

    internal override void Emit(UiNode node, UiDrawList draw)
    {
        draw.Nesting.PushOpacity(Alpha);
        var box = BoxOf(node);
        if ((Shadow ?? (ThemeShadow is { } size ? node.Environment.Theme.Shadows[size] : null)) is { } shadow && shadow.Color.A > 0f)
        {
            draw.AddShadow(box, Radius, shadow);
        }

        if (Fill is not null || BorderWidth > 0f)
        {
            draw.AddBox(box, Fill, Radius, new UiStroke(BorderWidth, BorderColor));
        }

        if (Clips)
        {
            draw.Nesting.PushClip(box, Radius);
        }
    }

    internal override void EmitAfter(UiNode node, UiDrawList draw)
    {
        if (Clips)
        {
            draw.Nesting.PopClip();
        }

        draw.Nesting.PopOpacity();
    }
}

internal sealed record StyleModifier(
    UiView Child, float? Size = null, FontWeight? Weight = null, Color? Color = null, bool? MonospacedDigits = null, int? LineLimit = null,
    GameFont? Font = null, bool? Italic = null, TextAlignment? Alignment = null, TextStyle? Style = null, Tone? Tone = null)
    : Modifier(Child)
{
    internal override UiEnvironment Inherit(UiEnvironment environment)
    {
        var text = environment.Text;
        var theme = environment.Theme;

        // A text style sets both size and weight, and a size or weight written with it wins.
        if (Style is { } role)
        {
            var face = theme.Typography[role];
            text = text with { Size = face.Size, Weight = face.Weight };
        }

        var color = Color ?? (Tone is { } tone ? theme.Colors[tone] : text.Color);
        text = new TextLook(
            Size ?? text.Size, Weight ?? text.Weight, color, MonospacedDigits ?? text.MonospacedDigits, LineLimit ?? text.LineLimit,
            Font ?? text.Font, Italic ?? text.Italic, Alignment ?? text.Alignment);
        return environment with { Text = text };
    }
}

/// <summary>Changes the theme, whether controls are turned off, or their size, for everything inside.</summary>
internal sealed record EnvironmentModifier(UiView Child, Theme? Theme = null, bool? Disabled = null, ControlSize? ControlSize = null) : Modifier(Child)
{
    internal override UiEnvironment Inherit(UiEnvironment environment)
    {
        // A new theme brings its own text colour, unless one was set inside an older theme on purpose.
        if (Theme is { } theme && theme != environment.Theme)
        {
            var text = environment.Text.Color == environment.Theme.Colors.Foreground ? environment.Text with { Color = theme.Colors.Foreground } : environment.Text;
            environment = environment with { Text = text };
        }

        // Once turned off, everything inside stays off.
        bool disabled = environment.Disabled || Disabled == true;
        return environment.With(Theme, disabled, ControlSize);
    }
}
