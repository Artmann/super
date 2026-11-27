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
        UiBodyHooks.Running?.UseSlot(initial, static value => new State<T>(value))
        ?? throw new InvalidOperationException("UseState only works while Body runs.");

    /// <summary>
    /// What this component sits in: the theme, whether controls are turned off, and how big they are.
    /// Read it in Body. When it changes, Body runs again.
    /// </summary>
    protected static UiEnvironment Environment =>
        UiBodyHooks.Running?.Environment ?? throw new InvalidOperationException("Environment only works while Body runs.");

    /// <summary>
    /// A number that eases toward <paramref name="target"/> over <paramref name="seconds"/> whenever the target
    /// changes, for a knob that slides or a panel that fades. It keeps Body running each frame until it
    /// arrives. Give <paramref name="from"/> to start there when the view appears, so it moves in. Call it
    /// from Body in the same order each run, as <see cref="UseState"/>. Inside <c>.Animations(false)</c> it
    /// returns the target at once.
    /// </summary>
    protected static float UseAnimation(float target, float seconds = 0.2f, Easing easing = Easing.Out, float? from = null) =>
        UiBodyHooks.Running?.Animate(target, seconds, easing, from)
        ?? throw new InvalidOperationException("UseAnimation only works while Body runs.");

    /// <summary>
    /// The interface's time in seconds, for something that keeps moving, such as a spinner. Body runs again
    /// every frame while it reads this. Inside <c>.Animations(false)</c> it stays at zero.
    /// </summary>
    protected static double UseTime() =>
        UiBodyHooks.Running?.Time() ?? throw new InvalidOperationException("UseTime only works while Body runs.");
}

/// <summary>A view Super draws itself, which makes it the end of the line for Body.</summary>
public abstract record PrimitiveView : UiView
{
    public sealed override UiView Body => throw new InvalidOperationException($"{GetType().Name} is drawn by Super and has no Body.");

    internal abstract IReadOnlyList<UiView?> Children { get; }

    /// <summary>Answers a parent's offer of space. The answer can be anything: the parent has to respect it.</summary>
    internal abstract UiSize Measure(UiNode node, Proposal proposal);

    internal abstract void Place(UiNode node, UiRect frame);

    /// <summary>Draws this view, behind its children.</summary>
    internal virtual void Emit(UiNode node, UiDrawList draw)
    {
    }

    /// <summary>Runs after the children, to end what Emit began and draw anything in front of them.</summary>
    internal virtual void EmitAfter(UiNode node, UiDrawList draw)
    {
    }

    /// <summary>Whether this stops the mouse where it's drawn. Text doesn't.</summary>
    internal virtual bool Blocks => false;

    /// <summary>Whether the mouse goes straight through this view and all that's inside it.</summary>
    internal virtual bool PassesPointer => false;

    /// <summary>The part of the screen the mouse can touch.</summary>
    internal virtual UiRect HitArea(UiNode node) => node.Frame;

    /// <summary>Where this cuts off what's inside it, if it does. What's cut off can't be hit.</summary>
    internal virtual UiRect? ClipArea(UiNode node) => null;

    /// <summary>Whether the mouse here misses this view and all that's inside it: it passes through, or it's cut off.</summary>
    internal bool Misses(UiNode node, float x, float y) => PassesPointer || ClipArea(node) is { } clip && !clip.Contains(x, y);

    /// <summary>Whether this takes the mouse here ahead of its children.</summary>
    internal virtual bool HitsFirst(UiNode node, float x, float y) => false;

    /// <summary>Changes the environment, such as the text style, for everything inside this view.</summary>
    internal virtual UiEnvironment Inherit(UiEnvironment environment) => environment;

    /// <summary>
    /// Whether this child shows: a hidden one is laid out but draws nothing, takes no pointer, and
    /// holds no focus or control.
    /// </summary>
    internal virtual bool Shows(UiNode node, int child) => true;

    /// <summary>Whether this node's child at this index shows. A component's one child always does.</summary>
    internal static bool IsShown(UiNode parent, int child) => parent.View is not PrimitiveView primitive || primitive.Shows(parent, child);

    /// <summary>
    /// Where a node shows: a component is where its body is, and a box is where it shows,
    /// inside its margin.
    /// </summary>
    internal static UiRect ShownArea(UiNode node)
    {
        while (node.View is not PrimitiveView)
        {
            node = node.Children[0]!;
        }

        return ((PrimitiveView)node.View).HitArea(node);
    }
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

    public float Right => X + Width;

    public float Bottom => Y + Height;

    public bool Contains(float x, float y) => x >= X && x < Right && y >= Y && y < Bottom;

    /// <summary>Whether the two share any area. An empty rectangle shares none.</summary>
    internal bool Overlaps(UiRect other) =>
        Width > 0f && Height > 0f && other.Width > 0f && other.Height > 0f
        && X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;

    /// <summary>The area both share. It has no size if they don't overlap.</summary>
    internal UiRect Intersect(UiRect other)
    {
        float left = Math.Max(X, other.X);
        float top = Math.Max(Y, other.Y);
        return new UiRect(left, top, Math.Max(Math.Min(Right, other.Right) - left, 0f), Math.Max(Math.Min(Bottom, other.Bottom) - top, 0f));
    }

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

/// <summary>
/// How text looks. It flows down the tree, so a style set on a stack reaches all the text inside. A line
/// limit of null lets text wrap onto as many lines as it needs, and a null font is the built-in one.
/// </summary>
internal readonly record struct TextLook(
    float Size,
    FontWeight Weight,
    Color Color,
    bool MonospacedDigits,
    int? LineLimit = null,
    GameFont? Font = null,
    bool Italic = false,
    TextAlignment Alignment = TextAlignment.Leading)
{
    public static TextLook Default { get; } = new(14f, FontWeight.Regular, Color.White, false);

    public bool Bold => Weight >= FontWeight.SemiBold;
}

/// <summary>
/// What flows down the tree to every view inside: how text looks, the theme, whether controls are turned
/// off, and how big they are. A component reads it in Body through <see cref="UiView.Environment"/>.
/// </summary>
public readonly record struct UiEnvironment
{
    internal UiEnvironment(TextLook text, Theme theme, bool disabled, ControlSize controlSize)
    {
        Text = text;
        Theme = theme;
        Disabled = disabled;
        ControlSize = controlSize;
    }

    /// <summary>The theme in force, <see cref="Theme.Dark"/> unless a view set another.</summary>
    public Theme Theme { get; }

    /// <summary>Whether a <c>.Disabled()</c> around this turned controls off.</summary>
    public bool Disabled { get; }

    /// <summary>How big controls are here, set with <c>.ControlSize(...)</c>.</summary>
    public ControlSize ControlSize { get; }

    /// <summary>The size of the theme's controls at <see cref="ControlSize"/>.</summary>
    public ControlMetrics Metrics => Theme.Metrics(ControlSize);

    /// <summary>The size in pixels text has here.</summary>
    public float FontSize => Text.Size;

    /// <summary>Whether things move here, which <c>.Animations(false)</c> turns off. Controls jump to where they're going without it.</summary>
    public bool Animates => !Still;

    /// <summary>What the player last played with, which <see cref="ButtonPrompt"/> and <see cref="ActionPrompt{TControl}"/> draw for.</summary>
    public InputDevice Device { get; init; }

    /// <summary>Whose gamepad is plugged in, which decides how gamepad prompts are drawn.</summary>
    public GamepadFamily Gamepad { get; init; } = GamepadFamily.Other;

    internal TextLook Text { get; init; }

    /// <summary>Whether motion is off here, so animations jump straight to where they're going.</summary>
    internal bool Still { get; init; }

    internal static UiEnvironment Default { get; } = For(Theme.Dark);

    /// <summary>Text starts in the theme's body style and colour.</summary>
    internal static UiEnvironment For(Theme theme) =>
        new(TextLook.Default with { Size = theme.Typography.Body.Size, Weight = theme.Typography.Body.Weight, Color = theme.Colors.Foreground }, theme, false, ControlSize.Regular);

    internal UiEnvironment With(Theme? theme = null, bool? disabled = null, ControlSize? controlSize = null) =>
        new(Text, theme ?? Theme, disabled ?? Disabled, controlSize ?? ControlSize) { Still = Still, Device = Device, Gamepad = Gamepad };
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
