namespace Super.Ui;

/// <summary>What a view that can hold focus does with the keys and the gamepad while it has it.</summary>
internal sealed record FocusKeys
{
    /// <summary>What Enter, Space and the gamepad's South button do. Null presses the click inside, if there is one.</summary>
    public Action? Activate { get; init; }

    /// <summary>What left and right do, with -1 or 1, for a slider or a picker. Null moves focus instead.</summary>
    public Action<int>? Adjust { get; init; }

    /// <summary>The text being edited, for a text field, which takes typing and the editing keys.</summary>
    public UiTextInput? Text { get; init; }

    /// <summary>Set while the view holds focus, so its Body can show it, as a text field shows its caret.</summary>
    public State<bool>? Focused { get; init; }

    /// <summary>Whether it's turned off, so focus passes it by.</summary>
    public bool Disabled { get; init; }
}

/// <summary>How a theme shows which control has focus.</summary>
public enum FocusLook
{
    /// <summary>A soft glow and a fill behind the control, with a thin edge, that slides from one control to the next. The default.</summary>
    Highlight,

    /// <summary>A thin ring a little outside the control, as on a web page.</summary>
    Ring,
}

/// <summary>A place focus can stop. Inside another one it's part of that one, so a stepper's buttons aren't stops of their own.</summary>
internal sealed record FocusModifier(UiView Child, FocusKeys Keys) : Modifier(Child)
{
    private const float RingGap = 2f;
    private const float RingWidth = 2f;
    private const float EdgeWidth = 1.5f;

    /// <summary>The highlight's glow and fill, behind the control, so a control with no fill of its own, such as a menu row, shows it.</summary>
    internal override void Emit(UiNode node, UiDrawList draw)
    {
        if (Marker(node) is not { } marker || node.Environment.Theme is not { Focus: FocusLook.Highlight } theme)
        {
            return;
        }

        var glow = new Shadow(theme.Colors.Ring with { A = 0.45f }, Blur: 14f, Y: 2f);
        draw.AddShadow(marker, theme.Radius, glow);
        draw.AddBox(marker, theme.Colors.Hover, theme.Radius);
    }

    /// <summary>The edge over the control: the highlight's thin one, or the ring, a little outside it.</summary>
    internal override void EmitAfter(UiNode node, UiDrawList draw)
    {
        if (node.Tree.Focus.Marker is not { } marker || node != node.Tree.Focus.Node)
        {
            return;
        }

        var theme = node.Environment.Theme;
        if (theme.Focus == FocusLook.Highlight)
        {
            draw.AddBox(marker, null, theme.Radius, new UiStroke(EdgeWidth, theme.Colors.Ring));
            return;
        }

        const float Out = RingGap + RingWidth;
        var ring = new UiRect(marker.X - Out, marker.Y - Out, marker.Width + 2f * Out, marker.Height + 2f * Out);
        draw.AddBox(ring, null, theme.Radius + Out, new UiStroke(RingWidth, theme.Colors.Ring));
    }

    /// <summary>Where the focus shows on this node this frame, on its way from the last control, or null when it doesn't show here.</summary>
    private UiRect? Marker(UiNode node)
    {
        var mark = node.Tree.Focus;
        if (!mark.ShowsRing(node))
        {
            return null;
        }

        return mark.Place(node, ShownArea(Inner(node)), node.Tree.Clock.Now, node.Environment.Still);
    }
}

/// <summary>Asks for focus for the first focusable view inside, when it appears and when navigation starts.</summary>
internal sealed record DefaultFocusModifier(UiView Child) : Modifier(Child);

/// <summary>Moving focus past the last stop inside comes round to the first, and back the other way.</summary>
internal sealed record FocusWrapModifier(UiView Child) : Modifier(Child);

/// <summary>
/// Which view holds focus, whether its ring shows, and where the focus marker is drawn. The marker slides from the
/// control it was on to the new one, within one layer: a popup that opens takes focus where it is.
/// </summary>
internal sealed class UiFocusMark
{
    private const double SlideSeconds = 0.14;

    private UiNode? _drawnOn;
    private UiNode? _lastDrawnOn;
    private UiRect? _lastMarker;
    private UiRect _from;
    private double _start = double.NegativeInfinity;

    /// <summary>The <see cref="FocusModifier"/> node with focus, or null.</summary>
    public UiNode? Node { get; set; }

    /// <summary>Whether the keyboard or gamepad was used since the mouse last was, like <c>:focus-visible</c>.</summary>
    public bool RingShown { get; set; }

    /// <summary>Where the marker was drawn in the last layout, in interface units, or null when focus didn't show.</summary>
    public UiRect? Marker { get; private set; }

    /// <summary>Whether the marker has reached the control, so nothing needs drawing again for it.</summary>
    public bool Landed { get; private set; } = true;

    /// <summary>Whether the ring shows round this node. A text field shows it whenever it's focused.</summary>
    public bool ShowsRing(UiNode node) => node == Node && (RingShown || node.View is FocusModifier { Keys.Text: not null });

    /// <summary>Before a layout draws: what the last one drew becomes where a slide starts from.</summary>
    public void StartDraw()
    {
        (_lastDrawnOn, _lastMarker) = (_drawnOn, Marker);
        _drawnOn = null;
        Marker = null;
        Landed = true;
    }

    /// <summary>Where the marker is at this time on the way to <paramref name="target"/>, the area of <paramref name="node"/>.</summary>
    public UiRect Place(UiNode node, UiRect target, double now, bool still)
    {
        if (node != _lastDrawnOn && _drawnOn != node)
        {
            bool slides = !still && _lastDrawnOn is { Mounted: true } last && _lastMarker is not null && Layer(last) == Layer(node);
            _from = _lastMarker ?? target;
            _start = slides ? now : double.NegativeInfinity;
        }

        float t = (float)Math.Clamp((now - _start) / SlideSeconds, 0.0, 1.0);
        float eased = 1f - (1f - t) * (1f - t) * (1f - t);
        var at = t >= 1f ? target : new UiRect(
            _from.X + (target.X - _from.X) * eased,
            _from.Y + (target.Y - _from.Y) * eased,
            _from.Width + (target.Width - _from.Width) * eased,
            _from.Height + (target.Height - _from.Height) * eased);
        _drawnOn = node;
        Marker = at;
        Landed = t >= 1f;
        return at;
    }

    /// <summary>The root of the layer this node is in: the whole screen, or the popup round it.</summary>
    private static UiNode Layer(UiNode node)
    {
        while (UiPointerSearch.Up(node) is { } up)
        {
            node = up;
        }

        return node;
    }
}
