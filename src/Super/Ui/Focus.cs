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

/// <summary>A place focus can stop. Inside another one it's part of that one, so a stepper's buttons aren't stops of their own.</summary>
internal sealed record FocusModifier(UiView Child, FocusKeys Keys) : Modifier(Child)
{
    private const float RingGap = 2f;
    private const float RingWidth = 2f;

    /// <summary>Draws the focus ring, a little outside the view, when the focus shows.</summary>
    internal override void EmitAfter(UiNode node, UiDrawList draw)
    {
        if (!node.Tree.Focus.ShowsRing(node))
        {
            return;
        }

        var theme = node.Environment.Theme;
        var area = ShownArea(Inner(node));
        const float Out = RingGap + RingWidth;
        var ring = new UiRect(area.X - Out, area.Y - Out, area.Width + 2f * Out, area.Height + 2f * Out);
        draw.AddBox(ring, null, theme.Radius + Out, new UiStroke(RingWidth, theme.Colors.Ring));
    }
}

/// <summary>Asks for focus for the first focusable view inside, when it appears and when navigation starts.</summary>
internal sealed record DefaultFocusModifier(UiView Child) : Modifier(Child);

/// <summary>Which view holds focus, and whether its ring shows, for drawing and for tests.</summary>
internal sealed class UiFocusMark
{
    /// <summary>The <see cref="FocusModifier"/> node with focus, or null.</summary>
    public UiNode? Node { get; set; }

    /// <summary>Whether the keyboard or gamepad was used since the mouse last was, like <c>:focus-visible</c>.</summary>
    public bool RingShown { get; set; }

    /// <summary>Whether the ring shows round this node. A text field shows it whenever it's focused.</summary>
    public bool ShowsRing(UiNode node) => node == Node && (RingShown || node.View is FocusModifier { Keys.Text: not null });
}
