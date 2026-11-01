using Rendition;

namespace Super.Ui;

/// <summary>
/// A description of a piece of interface. It's a cheap record that lives for moments, in
/// the style of SwiftUI: make a component by deriving from this and describing it in
/// <see cref="Body"/>. Its parameters are compared, so when they haven't changed, Body
/// doesn't run again.
/// </summary>
public abstract record UiView
{
    /// <summary>
    /// Names this view among its siblings, so it keeps its state when they move around.
    /// Set it with <see cref="UiExtensions.Id"/>. Without it, position is what counts.
    /// </summary>
    public object? Identity { get; init; }

    /// <summary>What this component is made of.</summary>
    public abstract UiView Body { get; }

    /// <summary>
    /// Keeps a value between runs of Body, for as long as this view stays in the tree.
    /// Call it from Body, the same number of times and in the same order each run. Only
    /// the components that read the value run again when it changes.
    /// </summary>
    protected static State<T> UseState<T>(T initial) =>
        UiNode.Rendering?.UseState(initial) ?? throw new InvalidOperationException("UseState only works while Body runs.");
}

/// <summary>A view Super draws itself, which makes it the end of the line for Body.</summary>
public abstract record PrimitiveView : UiView
{
    public sealed override UiView Body => throw new InvalidOperationException($"{GetType().Name} is drawn by Super and has no Body.");

    internal abstract IReadOnlyList<UiView?> Children { get; }

    /// <summary>Answers a parent's offer of space. The answer can be anything: the parent has to respect it.</summary>
    internal abstract UiSize Measure(UiNode node, Proposal proposal);

    internal abstract void Place(UiNode node, UiRect frame);

    internal virtual void Emit(UiNode node, UiTree tree)
    {
    }

    /// <summary>Changes the text style for everything inside this view.</summary>
    internal virtual TextStyle Inherit(TextStyle style) => style;
}

public enum HorizontalAlignment
{
    Leading,
    Center,
    Trailing,
}

public enum VerticalAlignment
{
    Top,
    Center,
    Bottom,
}

public enum Alignment
{
    TopLeading,
    Top,
    TopTrailing,
    Leading,
    Center,
    Trailing,
    BottomLeading,
    Bottom,
    BottomTrailing,
}

public readonly record struct UiSize(float Width, float Height);

public readonly record struct UiRect(float X, float Y, float Width, float Height)
{
    public UiSize Size => new(Width, Height);

    /// <summary>Puts something of the given size inside this rectangle.</summary>
    internal UiRect Align(UiSize size, Alignment alignment)
    {
        var (horizontal, vertical) = alignment.Fractions();
        return new UiRect(X + (Width - size.Width) * horizontal, Y + (Height - size.Height) * vertical, size.Width, size.Height);
    }
}

/// <summary>Lengths on each side of a rectangle, for padding and margins.</summary>
internal readonly record struct Edges(float Top, float Trailing, float Bottom, float Leading)
{
    private float Horizontal => Leading + Trailing;

    private float Vertical => Top + Bottom;

    public static Edges operator +(Edges a, Edges b) =>
        new(a.Top + b.Top, a.Trailing + b.Trailing, a.Bottom + b.Bottom, a.Leading + b.Leading);

    public Proposal Shrink(Proposal proposal) =>
        new(Shrink(proposal.Width, Horizontal), Shrink(proposal.Height, Vertical));

    public UiRect Shrink(UiRect frame) => new(
        frame.X + Leading,
        frame.Y + Top,
        Math.Max(frame.Width - Horizontal, 0f),
        Math.Max(frame.Height - Vertical, 0f));

    public UiSize Grow(UiSize size) => new(size.Width + Horizontal, size.Height + Vertical);

    private static float? Shrink(float? length, float amount) => length is { } value ? Math.Max(value - amount, 0f) : null;
}

/// <summary>
/// The space a parent offers. Null asks "how big would you like to be?", and infinity
/// asks "how far can you stretch?".
/// </summary>
internal readonly record struct Proposal(float? Width, float? Height);

/// <summary>How text looks. It flows down the tree, so a style set on a stack reaches all the text inside.</summary>
internal readonly record struct TextStyle(float Size, bool Bold, Color Color, bool MonospacedDigits)
{
    public static TextStyle Default { get; } = new(14f, false, Color.White, false);
}

internal static class AlignmentFractions
{
    public static (float Horizontal, float Vertical) Fractions(this Alignment alignment) => alignment switch
    {
        Alignment.TopLeading => (0f, 0f),
        Alignment.Top => (0.5f, 0f),
        Alignment.TopTrailing => (1f, 0f),
        Alignment.Leading => (0f, 0.5f),
        Alignment.Center => (0.5f, 0.5f),
        Alignment.Trailing => (1f, 0.5f),
        Alignment.BottomLeading => (0f, 1f),
        Alignment.Bottom => (0.5f, 1f),
        _ => (1f, 1f),
    };

    public static float Fraction(this HorizontalAlignment alignment) => (float)alignment / 2f;

    public static float Fraction(this VerticalAlignment alignment) => (float)alignment / 2f;
}
