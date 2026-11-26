namespace Super.Ui;

/// <summary>What a key or a gamepad button asks focus to do.</summary>
internal enum UiNav
{
    Next,
    Previous,
    Up,
    Down,
    Left,
    Right,
    Activate,
    Back,
}

/// <summary>
/// Moves focus with the keyboard and the gamepad, and hands a focused view the keys it uses. Keys come here
/// first, in the order they happened, and what focus doesn't take carries on to the game. A key it takes stays
/// taken until it's let go, so the game sees no part of it. Callbacks wait for <see cref="UiPointer.Deliver"/>.
/// </summary>
internal sealed class UiFocus(UiTree tree, Input input, List<Action> callbacks, int ticksPerSecond)
{
    private const int South = (int)GamepadButton.South;
    private const int East = (int)GamepadButton.East;

    private readonly HashSet<Key> _taken = [];
    private readonly UiPadRepeat _pad = new(ticksPerSecond);
    private readonly UiFocusTrap _trap = new(tree);
    private UiNode? _defaulted;

    private UiFocusMark Mark => tree.Focus;

    /// <summary>The focused view, while it's still on screen.</summary>
    private UiNode? Current => Mark.Node is { Mounted: true } node ? node : null;

    /// <summary>Handles the keys pressed since the last time this ran, and passes on the rest.</summary>
    public void PumpKeys()
    {
        foreach (var key in input.Keys.Events)
        {
            if (!Take(key))
            {
                input.Keys.Apply(key);
            }
        }

        input.Keys.Events.Clear();
    }

    /// <summary>Once a tick, before it rolls: the gamepad, and a default focus that just appeared.</summary>
    public void Tick()
    {
        // A control that goes away or turns off lets focus go.
        if (Mark.Node is { } focused && !UiFocusStops.IsOn(focused))
        {
            Focus(null, false);
        }

        // A popup that opens takes focus, and one that closes gives it back.
        if (_trap.Follow(Current) is (true, var to))
        {
            Focus(to, Mark.RingShown);
        }

        FocusDefault();
        Pad();
        input.Gamepad.LeftStickTaken = Current is not null;
    }

    /// <summary>
    /// The mouse went down on this node, or on the game with null. A text field takes focus and puts its caret
    /// there; anything else lets focus go, and the ring hides until a key moves it again.
    /// </summary>
    public void Pointed(UiNode? hit, float x)
    {
        var stop = UiFocusStops.Outermost(hit);
        if (stop is { View: FocusModifier { Keys: { Text: { } text, Disabled: false } } } && !stop.Environment.Disabled)
        {
            Focus(stop, false);
            if (UiFocusStops.Below<TextInputView>(stop) is { View: TextInputView shown } field)
            {
                text.Apply(text.Edit.MoveTo(shown.IndexAt(field, x), input.Keys.Shift), callbacks);
            }

            return;
        }

        Focus(null, false);
    }

    /// <summary>Whether focus takes this key event. Repeats only ever come here, and never reach the game.</summary>
    private bool Take(in KeyEvent key)
    {
        switch (key.Kind)
        {
            case KeyEventKind.Up:
                return _taken.Remove(key.Key);
            case KeyEventKind.Character:
                return Typed(key.Character);
            case KeyEventKind.Repeat:
                if (_taken.Contains(key.Key))
                {
                    Press(key.Key);
                }

                return true;
        }

        if (!Press(key.Key))
        {
            return false;
        }

        _taken.Add(key.Key);
        return true;
    }

    private bool Press(Key key)
    {
        if (Current is { View: FocusModifier { Keys.Text: { } text } } && !UiTextKeys.LeavesField(key))
        {
            return UiTextKeys.Press(key, text, input.Keys, callbacks);
        }

        return Nav(key) is { } nav && Command(nav);
    }

    private UiNav? Nav(Key key) => key switch
    {
        Key.Tab => input.Keys.Shift ? UiNav.Previous : UiNav.Next,
        Key.Up => UiNav.Up,
        Key.Down => UiNav.Down,
        Key.Left => UiNav.Left,
        Key.Right => UiNav.Right,
        Key.Enter or Key.KeyPadEnter or Key.Space => UiNav.Activate,
        Key.Escape => UiNav.Back,
        _ => null,
    };

    private bool Typed(char character)
    {
        if (Current is not { View: FocusModifier { Keys.Text: { } text } })
        {
            return false;
        }

        // Control characters aren't text, and Enter and Tab mean something else here.
        if (!char.IsControl(character))
        {
            text.Apply(text.Edit.Insert(character.ToString()), callbacks);
        }

        return true;
    }

    /// <summary>Does what a key or button asks. Returns whether focus used it, so the game shouldn't.</summary>
    private bool Command(UiNav nav)
    {
        // Back closes the popup on top, unless a text field there takes it first.
        if (nav == UiNav.Back && Current is not { View: FocusModifier { Keys.Text: not null } } && _trap.Back(callbacks))
        {
            return true;
        }

        if (Current is not { View: FocusModifier { Keys: var keys } } node)
        {
            return Start(nav);
        }

        bool text = keys.Text is not null;
        switch (nav)
        {
            case UiNav.Activate:
                Activate(node, keys);
                return true;
            case UiNav.Back:
                if (text)
                {
                    Focus(null, false);
                }

                return text;
        }

        if (!Mark.RingShown && !text)
        {
            // The first key after the mouse shows where focus is, before it moves it.
            Mark.RingShown = true;
            tree.RequestLayout();
            return true;
        }

        Move(node, keys, nav);
        return true;
    }

    private void Move(UiNode node, FocusKeys keys, UiNav nav)
    {
        if (nav is UiNav.Left or UiNav.Right && keys.Adjust is { } adjust)
        {
            int by = nav == UiNav.Left ? -1 : 1;
            callbacks.Add(() => adjust(by));
            return;
        }

        var stops = UiFocusStops.All(_trap.Root);
        var next = nav switch
        {
            UiNav.Next => UiFocusStops.InOrder(stops, node, 1),
            UiNav.Previous => UiFocusStops.InOrder(stops, node, -1),
            _ => UiFocusStops.Nearest(stops, node, nav),
        };
        if (next is not null)
        {
            Focus(next, true);
        }
    }

    /// <summary>With nothing focused, Tab starts at the first stop, and a direction at the default focus if there is one.</summary>
    private bool Start(UiNav nav)
    {
        var first = nav switch
        {
            UiNav.Next => UiFocusStops.All(_trap.Root).FirstOrDefault(),
            UiNav.Previous => UiFocusStops.All(_trap.Root).LastOrDefault(),
            UiNav.Up or UiNav.Down or UiNav.Left or UiNav.Right => UiFocusStops.Default(_trap.Root),
            _ => null,
        };
        if (first is null)
        {
            return false;
        }

        Focus(first, true);
        return true;
    }

    private void Activate(UiNode node, FocusKeys keys)
    {
        if (keys.Text is { } text)
        {
            text.Submit(callbacks);
        }
        else if ((keys.Activate ?? UiFocusStops.ClickInside(node)) is { } activate)
        {
            callbacks.Add(activate);
        }
    }

    /// <summary>Moves focus here, or lets it go with null, and scrolls it into view.</summary>
    private void Focus(UiNode? node, bool ring)
    {
        var old = Mark.Node;
        Mark.Node = node;
        Mark.RingShown = ring;
        tree.RequestLayout();
        if (old == node || node is null && old is null)
        {
            return;
        }

        if (old?.View is FocusModifier { Keys.Focused: { } was })
        {
            was.Value = false;
        }

        if (node?.View is FocusModifier { Keys: var keys })
        {
            if (keys.Focused is { } now)
            {
                now.Value = true;
            }

            if (keys.Text is { } text)
            {
                text.Apply(text.Edit.MoveTo(text.Edit.Text.Length, false), callbacks);
            }

            UiFocusStops.ScrollIntoView(tree, node);
        }
    }

    /// <summary>A view marked <see cref="ControlExtensions.DefaultFocus"/> takes focus when it appears, with its ring hidden.</summary>
    private void FocusDefault()
    {
        var marked = UiFocusStops.DefaultMark(_trap.Root);
        if (marked == _defaulted)
        {
            return;
        }

        _defaulted = marked;
        if (marked is not null && Current is null && UiFocusStops.Default(_trap.Root) is { } stop)
        {
            Focus(stop, false);
        }
    }

    private void Pad()
    {
        var buttons = input.Gamepad.Buttons;
        var (held, button) = UiPadRepeat.Held(input.Gamepad);
        if (_pad.Next(held) is { } nav)
        {
            if (Command(nav))
            {
                Mark.RingShown = true;
                if (button >= 0)
                {
                    buttons.Take(button);
                }
            }
            else
            {
                _pad.Refuse();
            }
        }

        if (buttons.WentDown(South) && Command(UiNav.Activate))
        {
            buttons.Take(South);
        }

        if (buttons.WentDown(East) && Command(UiNav.Back))
        {
            buttons.Take(East);
        }

        TabKeys.Bumpers(buttons, tree.Layers, callbacks);
    }
}

/// <summary>
/// Which way the d-pad or the left stick is held, and when that moves focus: at once, then again after a moment
/// and steadily while it's held, like a held key.
/// </summary>
internal sealed class UiPadRepeat(int ticksPerSecond)
{
    private const float StickPush = 0.5f;

    private static readonly (GamepadButton Button, UiNav Nav)[] DPad =
    [
        (GamepadButton.DPadUp, UiNav.Up),
        (GamepadButton.DPadDown, UiNav.Down),
        (GamepadButton.DPadLeft, UiNav.Left),
        (GamepadButton.DPadRight, UiNav.Right),
    ];

    private readonly int _delay = Math.Max(1, (int)MathF.Round(0.4f * ticksPerSecond));
    private readonly int _every = Math.Max(1, (int)MathF.Round(0.1f * ticksPerSecond));
    private UiNav? _held;
    private int _ticks;
    private bool _refused;

    /// <summary>The way the pad points this tick, and the d-pad button doing it, or -1 for the stick.</summary>
    public static (UiNav? Nav, int Button) Held(GamepadInput pad)
    {
        foreach (var (button, nav) in DPad)
        {
            if (pad.Buttons.Physical((int)button) || pad.Buttons.WentDown((int)button))
            {
                return (nav, (int)button);
            }
        }

        var stick = pad.PendingLeftStick;
        if (MathF.Max(MathF.Abs(stick.X), MathF.Abs(stick.Y)) < StickPush)
        {
            return (null, -1);
        }

        // Forward on the stick is positive y, and up the screen.
        var way = MathF.Abs(stick.X) > MathF.Abs(stick.Y)
            ? stick.X > 0f ? UiNav.Right : UiNav.Left
            : stick.Y > 0f ? UiNav.Up : UiNav.Down;
        return (way, -1);
    }

    /// <summary>Where to move this tick, if anywhere, while the pad points <paramref name="held"/>.</summary>
    public UiNav? Next(UiNav? held)
    {
        if (held != _held)
        {
            _held = held;
            _ticks = 0;
            _refused = false;
            return held;
        }

        if (held is null || _refused)
        {
            return null;
        }

        _ticks++;
        return _ticks >= _delay && (_ticks - _delay) % _every == 0 ? held : null;
    }

    /// <summary>Focus didn't want this push, so it belongs to the game until the pad points somewhere else.</summary>
    public void Refuse() => _refused = true;
}
