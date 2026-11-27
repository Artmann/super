using System.Text;

namespace Super;

/// <summary>What happened on the keyboard, in the order it happened, while it waits for the interface to look.</summary>
internal enum KeyEventKind
{
    Down,
    Up,

    /// <summary>The system repeating a held key. Only the interface uses these; the game never sees them.</summary>
    Repeat,

    /// <summary>A character typed, for text fields.</summary>
    Character,
}

internal readonly record struct KeyEvent(KeyEventKind Kind, Key Key, char Character = '\0');

/// <summary>
/// Text the player copies and pastes. Out of a window it's only kept here, so a test can copy and paste
/// without touching the system's clipboard. The window swaps in one that reaches the system's.
/// </summary>
internal class GameClipboard
{
    public virtual string Text { get; set; } = string.Empty;
}

/// <summary>
/// The keyboard as the window and scripts feed it. While <see cref="Routes"/> is on, each key waits in
/// <see cref="Events"/> for the interface, which takes what it uses and applies the rest with <see cref="Apply"/>.
/// </summary>
internal sealed class KeyFeed(InputTick tick)
{
    private readonly StringBuilder _typing = new();

    public ButtonSet Buttons { get; } = new(Input.KeyCount);

    /// <summary>When set, keys wait in <see cref="Events"/> for the interface to pass on what it doesn't take.</summary>
    public bool Routes { get; set; }

    /// <summary>What the keyboard did that the interface hasn't seen yet, in order.</summary>
    public List<KeyEvent> Events { get; } = [];

    /// <summary>What copy and paste in a text field use.</summary>
    public GameClipboard Clipboard { get; set; } = new();

    /// <summary>Whether Shift is down right now, between ticks, for the interface to read as a press happens.</summary>
    public bool Shift => Held(Key.LeftShift) || Held(Key.RightShift);

    /// <summary>Whether Control is down right now, or Command on a Mac, which does there what Control does elsewhere.</summary>
    public bool Command => Held(Key.LeftControl) || Held(Key.RightControl)
        || (OperatingSystem.IsMacOS() && (Held(Key.LeftSuper) || Held(Key.RightSuper)));

    public bool Alt => Held(Key.LeftAlt) || Held(Key.RightAlt);

    public void Down(Key key)
    {
        tick.PendingDevice = InputDevice.KeyboardAndMouse;
        Route(new KeyEvent(KeyEventKind.Down, key));
    }

    public void Up(Key key) => Route(new KeyEvent(KeyEventKind.Up, key));

    /// <summary>The system repeating a held key, which only the interface hears.</summary>
    public void Repeated(Key key)
    {
        if (Routes)
        {
            Events.Add(new KeyEvent(KeyEventKind.Repeat, key));
        }
    }

    public void Typed(char character) => Route(new KeyEvent(KeyEventKind.Character, default, character));

    /// <summary>A key event the interface didn't take, or no interface is there to take.</summary>
    public void Apply(in KeyEvent key)
    {
        switch (key.Kind)
        {
            case KeyEventKind.Down:
                Buttons.Press((int)key.Key);
                break;
            case KeyEventKind.Up:
                Buttons.Release((int)key.Key);
                break;
            case KeyEventKind.Character:
                _typing.Append(key.Character);
                break;
        }
    }

    /// <summary>Rolls the keys over to the next tick, and gives back what was typed since the last one.</summary>
    public string Roll()
    {
        Buttons.Roll();
        string typed = _typing.Length == 0 ? string.Empty : _typing.ToString();
        _typing.Clear();
        return typed;
    }

    private bool Held(Key key) => Buttons.Physical((int)key);

    private void Route(in KeyEvent key)
    {
        if (Routes)
        {
            Events.Add(key);
        }
        else
        {
            Apply(key);
        }
    }
}
