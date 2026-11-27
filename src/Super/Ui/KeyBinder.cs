namespace Super.Ui;

/// <summary>
/// A settings row that rebinds one action: its name, and the key or button that does it. A click, Enter or South sets
/// it listening, and the next key, mouse button or gamepad button pressed, as <see cref="Device"/> says, calls
/// <paramref name="OnBind"/> with it on the next tick. Escape stops listening and changes
/// nothing. The game keeps the binding, usually in an <see cref="Actions{TControl}"/>, and passes it back.
/// </summary>
public sealed record KeyBinder(string Label, Binding Binding, Action<Binding>? OnBind = null) : UiView
{
    /// <summary>What it listens for: keys and mouse buttons, or gamepad buttons.</summary>
    public InputDevice Device { get; init; } = InputDevice.KeyboardAndMouse;

    /// <summary>
    /// The binding it started with. When the binding is something else, a Reset button beside it puts this back
    /// through <c>OnBind</c>.
    /// </summary>
    public Binding? Default { get; init; }

    /// <summary>What else the binding does, such as "Jump", shown as a warning beside it, or null when nothing does.</summary>
    public string? Clash { get; init; }

    public bool Disabled { get; init; }

    public override UiView Body
    {
        get
        {
            var environment = Environment;
            var metrics = environment.Metrics;
            var listening = UseState(false);
            bool off = Disabled || environment.Disabled;
            bool now = listening.Value && !off;
            string shown = now ? Waiting : Name(environment.Gamepad);
            var look = UseState(default(PressState));
            UiView face = Row(now, environment, look.Value);
            if (now)
            {
                var bind = OnBind;
                face = new CaptureModifier(face, Device, binding =>
                {
                    listening.Value = false;
                    if (binding is { } chosen)
                    {
                        bind?.Invoke(chosen);
                    }
                });
            }

            var click = new ClickModifier(face, () => listening.Value = true, look, off);
            var meaning = new SemanticsModifier(click, new UiSemantics(UiRole.Button, Label, shown) { Disabled = off });
            var row = new FocusModifier(meaning, new FocusKeys { Activate = () => listening.Value = true, Disabled = off });
            return new HStack(Spacing: metrics.Gap)
            {
                row,
                Default is { } start && start != Binding
                    ? new Button("Reset", OnBind is { } reset ? () => reset(start) : null, off) { Style = ButtonStyle.Ghost }
                    : null,
            };
        }
    }

    private string Waiting => Device == InputDevice.Gamepad ? "Press a button" : "Press a key";

    private string Name(GamepadFamily family) => string.Join(" ", PromptGlyphs.Of(Binding, family).Select(glyph => glyph.Name));

    /// <summary>The name, then the warning if there's a clash, then the binding or the word that it's listening.</summary>
    private UiView Row(bool listening, UiEnvironment environment, PressState look)
    {
        var theme = environment.Theme;
        var metrics = environment.Metrics;
        // A slow pulse while it waits, so the row reads as asking for something.
        float pulse = listening ? 0.55f + 0.45f * MathF.Cos((float)UseTime() * MathF.Tau) : 1f;
        UiView key = listening
            ? new Text(Waiting).FontSize(metrics.FontSize).Weight(FontWeight.Medium).Foreground(theme.Colors.Primary).Opacity(pulse)
            : new ButtonPrompt(Binding);
        var row = new HStack(Spacing: metrics.Gap)
        {
            new Text(Label).FontSize(metrics.FontSize).LineLimit(1),
            new Spacer(),
            Clash is { } other && !listening
                ? new Label("Also " + other, new Icon(IconName.Info)).FontSize(metrics.FontSize * 0.9f).Foreground(theme.Colors.Destructive)
                : null,
            key,
        };
        var fill = listening ? theme.Colors.Hover : look.Hovered ? theme.Colors.Hover : default;
        return row
            .Padding(metrics.Padding, 0f)
            .Frame(maxWidth: UiExtensions.Infinity, minHeight: metrics.Height + metrics.Gap)
            .Background(fill)
            .Border(listening ? 1f : 0f, theme.Colors.Primary)
            .CornerRadius(theme.Radius)
            .Opacity(Disabled || environment.Disabled ? 0.5f : 1f);
    }
}

/// <summary>
/// A view listening for a binding. The next key, mouse button or gamepad button that <paramref name="Device"/> takes,
/// or any of them when it's null, goes to <paramref name="Capture"/>, and null goes there when Escape stops it.
/// </summary>
internal sealed record CaptureModifier(UiView Child, InputDevice? Device, Action<Binding?> Capture) : Modifier(Child);

/// <summary>Finds a <see cref="CaptureModifier"/> listening for a binding, and hands it what was pressed.</summary>
internal static class UiListening
{
    /// <summary>The view listening under this root, the popup on top or the tree, or null when none is.</summary>
    public static CaptureModifier? In(UiNode? root) => root is null ? null : UiFocusStops.Below<CaptureModifier>(root)?.View as CaptureModifier;

    /// <summary>
    /// Hands a key to the view listening under this root, where Escape stops it listening. Returns whether one is
    /// listening, so the key, its repeats and its up go nowhere else.
    /// </summary>
    public static bool Key(UiNode? root, in KeyEvent key, UiTakenKeys taken, List<Action> callbacks)
    {
        if (key.Kind == KeyEventKind.Up || In(root) is not { } listening)
        {
            return false;
        }

        if (key.Kind == KeyEventKind.Down)
        {
            taken.Add(key.Key);
            Give(listening, key.Key == Super.Key.Escape ? null : key.Key, InputDevice.KeyboardAndMouse, callbacks);
        }

        return true;
    }

    /// <summary>Hands a mouse button to the view listening, wherever it was pressed, and returns whether one was.</summary>
    public static bool Mouse(UiNode? root, MouseButton button, List<Action> callbacks)
    {
        if (In(root) is not { } listening)
        {
            return false;
        }

        Give(listening, button, InputDevice.KeyboardAndMouse, callbacks);
        return true;
    }

    /// <summary>
    /// Hands the first pad button that went down to the view listening, and takes it from the game. Returns whether
    /// one is listening while a button went down or the pad points somewhere, so the pad moves nothing else.
    /// </summary>
    public static bool Pad(UiNode? root, ButtonSet buttons, bool pointing, List<Action> callbacks)
    {
        int down = FirstDown(buttons);
        if (down < 0 && !pointing || In(root) is not { } listening)
        {
            return false;
        }

        if (down >= 0)
        {
            buttons.Take(down);
            Give(listening, (GamepadButton)down, InputDevice.Gamepad, callbacks);
        }

        return true;
    }

    /// <summary>
    /// Hands a press from this device to the view listening, on the next tick, if it listens for that device. Null
    /// stops it listening. A press from the other device is swallowed, so it does nothing else while it listens.
    /// </summary>
    private static void Give(CaptureModifier listening, Binding? binding, InputDevice device, List<Action> callbacks)
    {
        if (binding is null || listening.Device is null || listening.Device == device)
        {
            callbacks.Add(() => listening.Capture(binding));
        }
    }

    /// <summary>The first gamepad button that went down since the last tick, or -1.</summary>
    private static int FirstDown(ButtonSet buttons)
    {
        for (int i = 0; i < GamepadInput.ButtonCount; i++)
        {
            if (buttons.WentDown(i))
            {
                return i;
            }
        }

        return -1;
    }
}
