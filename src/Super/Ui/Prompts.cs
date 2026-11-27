namespace Super.Ui;

/// <summary>
/// A binding drawn as the key or button it stands for, in the same size as the text around it. A key is a keycap,
/// a mouse button is a mouse with that button filled in, and a gamepad button is drawn the way its pad marks it:
/// coloured letters on an Xbox pad, shapes on a PlayStation one, and Nintendo's letters where they sit. A binding of
/// several keys, such as <see cref="Keys.WASD"/>, shows each of them.
/// </summary>
/// <example><code>new ButtonPrompt(GamepadButton.South)</code></example>
public sealed record ButtonPrompt(Binding Binding) : UiView
{
    /// <summary>Whose pad to draw for, or null for the pad plugged in.</summary>
    public GamepadFamily? Family { get; init; }

    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var glyphs = PromptGlyphs.Of(Binding, Family ?? environment.Gamepad);
            if (glyphs.Count == 1)
            {
                return Glyph(glyphs[0], environment);
            }

            var row = new HStack(Spacing: MathF.Round(environment.FontSize * 0.25f));
            foreach (var glyph in glyphs)
            {
                row.Add(Glyph(glyph, environment));
            }

            return row;
        }
    }

    private static UiView Glyph(PromptGlyph glyph, UiEnvironment environment)
    {
        UiView face = glyph.Icon is { } icon ? new Icon(icon) : new Text(glyph.Text ?? glyph.Name);
        var drawn = glyph.Shape switch
        {
            GlyphShape.Keycap => KeyHint.Cap(face, environment),
            GlyphShape.Mouse => Mouse(face, environment),
            _ => PadButton(face, glyph, environment),
        };
        return new SemanticsModifier(drawn, new UiSemantics(UiRole.Prompt, glyph.Name, glyph.Device));
    }

    /// <summary>A mouse as tall as a keycap, in the text's colour.</summary>
    private static UiView Mouse(UiView face, UiEnvironment environment)
    {
        var (_, side) = KeyHint.Sizes(environment);
        return face.FontSize(MathF.Round(side / 1.15f)).Foreground(environment.Theme.Colors.Foreground);
    }

    // A pad's buttons are dark plastic whatever the theme, so the marks on them read as they do in the hand.
    private static readonly Color Plastic = Color.Hex("#26282D");
    private static readonly Color Mark = Color.Hex("#F4F4F5");
    private static readonly Color Rim = Color.Hex("#4A4D55");

    /// <summary>A round face button, or a rounded long one for bumpers, triggers and the menu buttons.</summary>
    private static UiView PadButton(UiView face, PromptGlyph glyph, UiEnvironment environment)
    {
        var (size, side) = KeyHint.Sizes(environment);
        bool round = glyph.Shape == GlyphShape.Disc;
        // A word on a round button would spill out of it, so it gets a smaller size.
        float text = round && (glyph.Text?.Length ?? 0) > 1 ? MathF.Round(size * 0.8f) : size;
        var marked = face.FontSize(text).Weight(FontWeight.Bold).Foreground(glyph.Tint ?? Mark).LineLimit(1);
        var shaped = round
            ? marked.Frame(width: side, height: side)
            : marked.Padding(MathF.Round(size * 0.55f), 0f).Frame(minWidth: MathF.Round(side * 1.3f), minHeight: side);
        return shaped
            .Background(Plastic)
            .Border(1f, Rim)
            .CornerRadius(round ? side / 2f : MathF.Round(side * 0.4f));
    }
}

/// <summary>
/// What does an action, for the device the player is on: the first key or mouse binding while they play with the
/// keyboard and mouse, and the first gamepad binding once they pick up a pad, drawn as a <see cref="ButtonPrompt"/>
/// with an optional label after it. It changes as soon as the player switches, and follows a rebind on the next
/// update. On a touch screen it shows nothing, as there are no buttons to point at.
/// </summary>
/// <example><code>new ActionPrompt&lt;Control&gt;(actions, Control.Jump, "Jump")</code></example>
public sealed record ActionPrompt<TControl>(Actions<TControl> Actions, TControl Control, string? Label = null) : UiView
    where TControl : struct, Enum
{
    // The bindings as they were when this was made, so a rebind compares unequal and the prompt is drawn again.
    private readonly Binding[] _bindings = [.. Actions.Bindings(Control)];

    /// <summary>Whose pad to draw for, or null for the pad plugged in.</summary>
    public GamepadFamily? Family { get; init; }

    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var row = new HStack(Spacing: MathF.Round(environment.FontSize * 0.5f));
            if (environment.Device == InputDevice.Touch || Pick(environment.Device == InputDevice.Gamepad) is not { } binding)
            {
                return row;
            }

            row.Add(new ButtonPrompt(binding) { Family = Family });
            if (Label is { } label)
            {
                row.Add(new Text(label));
            }

            return row;
        }
    }

    public bool Equals(ActionPrompt<TControl>? other) =>
        base.Equals(other)
        && ReferenceEquals(Actions, other.Actions)
        && EqualityComparer<TControl>.Default.Equals(Control, other.Control)
        && Label == other.Label
        && Family == other.Family
        && _bindings.AsSpan().SequenceEqual(other._bindings);

    public override int GetHashCode() => HashCode.Combine(Actions, Control, Label, Family, _bindings.Length);

    private Binding? Pick(bool gamepad)
    {
        foreach (var binding in _bindings)
        {
            if (PromptGlyphs.OnGamepad(binding) == gamepad)
            {
                return binding;
            }
        }

        return null;
    }
}
