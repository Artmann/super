namespace Super.Ui;

/// <summary>
/// The modifiers. Each wraps a view, so their order matters: padding then background
/// differs from the reverse. The exception is the box: background, border, corner radius,
/// opacity and margin written next to each other are one box, in any order.
/// </summary>
public static class UiExtensions
{
    /// <summary>Stretch to fill, as in <c>.Frame(maxWidth: UiExtensions.Infinity)</c>.</summary>
    public const float Infinity = float.PositiveInfinity;

    public static UiView Padding(this UiView view, float all) => view.Padding(all, all, all, all);

    public static UiView Padding(this UiView view, float horizontal, float vertical) =>
        view.Padding(vertical, horizontal, vertical, horizontal);

    public static UiView Padding(this UiView view, float top, float trailing, float bottom, float leading) =>
        new PaddingModifier(view, new Edges(top, trailing, bottom, leading));

    /// <summary>Space outside the view, which its background doesn't fill. Margins next to each other add up.</summary>
    public static UiView Margin(this UiView view, float all) => view.Margin(all, all, all, all);

    public static UiView Margin(this UiView view, float horizontal, float vertical) =>
        view.Margin(vertical, horizontal, vertical, horizontal);

    public static UiView Margin(this UiView view, float top, float trailing, float bottom, float leading)
    {
        var box = Box(view);
        return box with { Margin = box.Margin + new Edges(top, trailing, bottom, leading) };
    }

    /// <summary>
    /// Gives the view a fixed size, or limits it. A maximum lets it stretch up to that
    /// length, as flexbox's grow does, and a minimum holds it open. A view smaller than
    /// the frame sits where <paramref name="alignment"/> says.
    /// </summary>
    public static UiView Frame(
        this UiView view,
        float? width = null,
        float? height = null,
        float? maxWidth = null,
        float? maxHeight = null,
        Alignment alignment = Alignment.Center,
        float? minWidth = null,
        float? minHeight = null) =>
        new FrameModifier(view, width, height, maxWidth, maxHeight, alignment, minWidth, minHeight);

    /// <summary>Makes the view the size it would like to be, rather than the size it's offered.</summary>
    public static UiView FixedSize(this UiView view) => new FixedSizeModifier(view);

    /// <summary>Fills the view's whole area behind it.</summary>
    /// <remarks>A colour is a paint, and so is a gradient: <c>.Background(Paint.Linear(top, bottom))</c>.</remarks>
    public static UiView Background(this UiView view, Paint paint) => Box(view) with { Fill = paint };

    /// <summary>Rounds this view's background, border and image, and clips what's inside it.</summary>
    public static UiView CornerRadius(this UiView view, float radius) => Box(view) with { Radius = radius };

    /// <summary>
    /// Rounds each corner by its own amount, such as only the top ones of a tab:
    /// <c>.CornerRadius(new CornerRadii(8f, 8f, 0f, 0f))</c>.
    /// </summary>
    public static UiView CornerRadius(this UiView view, CornerRadii radii) => Box(view) with { Radius = radii };

    /// <summary>
    /// Casts a soft shadow under this view's box, which follows its corners. It sits behind the box, so
    /// a see-through background doesn't show it.
    /// </summary>
    public static UiView Shadow(this UiView view, Shadow shadow) => Box(view) with { Shadow = shadow, ThemeShadow = null };

    /// <summary>Casts the theme's small, medium or large shadow, so it follows a change of theme.</summary>
    public static UiView Shadow(this UiView view, ShadowSize size) => Box(view) with { Shadow = null, ThemeShadow = size };

    /// <summary>Casts a shadow of this colour, blurred this far and moved this far down and to the right.</summary>
    public static UiView Shadow(this UiView view, Color color, float blur, float x = 0f, float y = 0f, float spread = 0f) =>
        view.Shadow(new Shadow(color, blur, y, x, spread));

    /// <summary>A line inside the view's edge, following its corners.</summary>
    public static UiView Border(this UiView view, float width, Color color) =>
        Box(view) with { BorderWidth = width, BorderColor = color };

    /// <summary>Fades this view and everything inside it, from 0 to 1.</summary>
    public static UiView Opacity(this UiView view, float value) => Box(view) with { Alpha = value };

    /// <summary>Names this view among its siblings, so it keeps its state when they move.</summary>
    public static UiView Id(this UiView view, object identity) => view with { Identity = identity };

    /// <summary>Runs this on a left click, on the next tick. Use <see cref="Pressable"/> to look different when hovered.</summary>
    public static UiView OnClick(this UiView view, Action click) => new ClickModifier(view, click);

    /// <summary>Lets the mouse through to whatever's behind, as CSS's <c>pointer-events: none</c> does.</summary>
    public static UiView IgnoresPointer(this UiView view) => new IgnoresPointerModifier(view);

    /// <summary>Lets the left button carry <paramref name="payload"/> from here. <paramref name="preview"/> follows the cursor.</summary>
    public static UiView Draggable<T>(this UiView view, T payload, UiView? preview = null)
        where T : notnull =>
        new DragModifier(view, start => start.Button == MouseButton.Left ? payload : null, _ => preview);

    /// <summary>
    /// Lets any mouse button carry something from here, chosen at the press: <paramref name="grab"/> is told the
    /// button and the modifier keys held, and returns what to carry, or null for a press that doesn't drag. It
    /// only chooses, so it mustn't change the game. <paramref name="preview"/> follows the cursor, made from what's carried.
    /// </summary>
    public static UiView Draggable<T>(this UiView view, Func<DragStart, T?> grab, Func<T, UiView?>? preview = null)
        where T : class =>
        new DragModifier(view, start => grab(start), payload => preview?.Invoke((T)payload));

    /// <summary>
    /// Takes what's dropped here, on the next tick. <paramref name="canDrop"/> can turn
    /// down a payload, which then drops nowhere and looks like nothing's here.
    /// </summary>
    public static UiView DropTarget<T>(this UiView view, Action<T> onDrop, Func<T, bool>? canDrop = null) =>
        new DropModifier<T>(view, onDrop, canDrop);

    /// <summary>Shows <paramref name="content"/> near the cursor after a short hover.</summary>
    public static UiView Tooltip(this UiView view, UiView content) => new TooltipModifier(view, content);

    /// <summary>
    /// Gives this view, and everything inside it, a theme: the colours, corners and sizes that built-in
    /// controls take. Text that had the old theme's foreground takes the new one's.
    /// </summary>
    public static UiView Theme(this UiView view, Theme theme) => new EnvironmentModifier(view, Theme: theme);

    /// <summary>
    /// Turns off the controls in this view, and in everything inside it: they fade, and clicks, keys and
    /// touches pass them by. Turning off can't be undone further in.
    /// </summary>
    public static UiView Disabled(this UiView view, bool disabled = true) => new EnvironmentModifier(view, Disabled: disabled);

    /// <summary>Makes the built-in controls in this view, and in everything inside it, small, regular or large.</summary>
    public static UiView ControlSize(this UiView view, ControlSize size) => new EnvironmentModifier(view, ControlSize: size);

    /// <summary>Continues the box this view already is, so neighbouring box modifiers make one box.</summary>
    private static BoxModifier Box(UiView view) => view as BoxModifier ?? new BoxModifier(view);
}
