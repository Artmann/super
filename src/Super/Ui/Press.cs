namespace Super.Ui;

/// <summary>How the mouse stands with something that can be clicked.</summary>
public readonly record struct PressState(bool Hovered, bool Pressed);

/// <summary>
/// Something clickable, drawn however a game likes: <paramref name="Content"/> describes
/// it for each state. A click is a left press and release on it. The click runs on the
/// next tick, just before Update, so it may change the game.
/// </summary>
public sealed record Pressable(Func<PressState, UiView> Content, Action? OnClick = null, bool Disabled = false) : UiView
{
    public override UiView Body
    {
        get
        {
            var look = UseState(default(PressState));
            return new ClickModifier(Content(Disabled ? default : look.Value), OnClick, look, Disabled);
        }
    }
}

/// <summary>A labeled button. A disabled one is dimmed and doesn't react, but still blocks the mouse.</summary>
public sealed record Button(string Label, Action? OnClick = null, bool Disabled = false) : UiView
{
    private static readonly Color Resting = new(0.24f, 0.27f, 0.33f);
    private static readonly Color Hovered = new(0.31f, 0.36f, 0.45f);
    private static readonly Color Held = new(0.18f, 0.21f, 0.27f);

    public override UiView Body => new Pressable(
        look => new Text(Label)
            .Padding(14f, 8f)
            .Background(look.Pressed ? Held : look.Hovered ? Hovered : Resting)
            .CornerRadius(6f)
            .Opacity(Disabled ? 0.45f : 1f),
        OnClick,
        Disabled);
}

/// <summary>A modifier the mouse can touch. It covers what its view draws, so a margin stays out of reach.</summary>
internal abstract record PointerModifier(UiView Child) : Modifier(Child)
{
    internal override bool Blocks => true;

    internal override UiRect HitArea(UiNode node)
    {
        var inner = Inner(node);
        while (inner.View is not PrimitiveView)
        {
            inner = inner.Children[0]!;
        }

        return ((PrimitiveView)inner.View).HitArea(inner);
    }
}

internal sealed record ClickModifier(UiView Child, Action? Click, State<PressState>? Look = null, bool Disabled = false) : PointerModifier(Child);

/// <summary>Lets the mouse through this view and everything inside it.</summary>
internal sealed record IgnoresPointerModifier(UiView Child) : Modifier(Child)
{
    internal override bool PassesPointer => true;
}

internal sealed record DragModifier(UiView Child, object Payload, UiView? Preview) : PointerModifier(Child);

/// <summary>Lets the pointer offer a payload without knowing its type.</summary>
internal abstract record DropModifier(UiView Child) : PointerModifier(Child)
{
    public abstract bool Accepts(object payload);

    public abstract void Drop(object payload);
}

internal sealed record DropModifier<T>(UiView Child, Action<T> OnDrop, Func<T, bool>? CanDrop) : DropModifier(Child)
{
    public override bool Accepts(object payload) => payload is T value && (CanDrop?.Invoke(value) ?? true);

    public override void Drop(object payload) => OnDrop((T)payload);
}

internal sealed record TooltipModifier(UiView Child, UiView Content) : PointerModifier(Child);
