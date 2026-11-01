using System.Numerics;

namespace Super;

/// <summary>
/// One way to perform an action: a key, a button, a stick, a trigger, the mouse, or a
/// few keys acting as an axis or a direction pad. Keys and buttons convert to one
/// implicitly, so <c>actions.Bind(Control.Jump, Key.Space, GamepadButton.South)</c> reads
/// naturally. It holds no references, so a list of them is plain data.
/// </summary>
public readonly record struct Binding
{
    internal BindingKind Kind { get; private init; }

    internal Source First { get; private init; }

    internal Source Second { get; private init; }

    internal Source Third { get; private init; }

    internal Source Fourth { get; private init; }

    /// <summary>Multiplies what this binding contributes. Use it to make mouse pixels and a stick mean the same thing.</summary>
    public float Scale { get; private init; }

    public Binding()
    {
        Scale = 1f;
    }

    /// <summary>The cursor's movement this tick, in pixels. Never clamped.</summary>
    public static Binding MouseDelta { get; } = new() { Kind = BindingKind.MouseDelta };

    /// <summary>The wheel's notches this tick, on the x axis. Never clamped.</summary>
    public static Binding MouseScroll { get; } = new() { Kind = BindingKind.MouseScroll };

    public static implicit operator Binding(Key key) => Button(new Source(SourceKind.Key, (int)key));

    public static implicit operator Binding(MouseButton button) => Button(new Source(SourceKind.MouseButton, (int)button));

    public static implicit operator Binding(GamepadButton button) => Button(new Source(SourceKind.GamepadButton, (int)button));

    public static implicit operator Binding(GamepadStick stick) => new() { Kind = BindingKind.Stick, First = new Source(SourceKind.GamepadStick, (int)stick) };

    public static implicit operator Binding(GamepadTrigger trigger) => new() { Kind = BindingKind.Trigger, First = new Source(SourceKind.GamepadTrigger, (int)trigger) };

    /// <summary>Two keys, buttons or triggers as one axis from -1 to 1.</summary>
    public static Binding Axis(Binding negative, Binding positive) => new()
    {
        Kind = BindingKind.Axis,
        First = Scalar(negative, nameof(negative)),
        Second = Scalar(positive, nameof(positive)),
    };

    /// <summary>Four keys, buttons or triggers as a direction, with up giving positive y.</summary>
    public static Binding Composite(Binding up, Binding down, Binding left, Binding right) => new()
    {
        Kind = BindingKind.Composite,
        First = Scalar(up, nameof(up)),
        Second = Scalar(down, nameof(down)),
        Third = Scalar(left, nameof(left)),
        Fourth = Scalar(right, nameof(right)),
    };

    /// <summary>The same binding with its contribution multiplied by <paramref name="scale"/>.</summary>
    public Binding Scaled(float scale) => this with { Scale = scale };

    private static Binding Button(Source source) => new() { Kind = BindingKind.Button, First = source };

    private static Source Scalar(Binding binding, string parameter) =>
        binding.Kind is BindingKind.Button or BindingKind.Trigger
            ? binding.First
            : throw new ArgumentException("An axis or composite takes keys, buttons or triggers, not sticks or the mouse.", parameter);
}

/// <summary>Common keyboard directions.</summary>
public static class Keys
{
    public static Binding WASD { get; } = Binding.Composite(Key.W, Key.S, Key.A, Key.D);

    public static Binding Arrows { get; } = Binding.Composite(Key.Up, Key.Down, Key.Left, Key.Right);
}

/// <summary>The mouse as a binding source.</summary>
public static class Mouse
{
    public static Binding Delta => Binding.MouseDelta;

    public static Binding Scroll => Binding.MouseScroll;
}

internal enum BindingKind
{
    Button,
    Trigger,
    Stick,
    Axis,
    Composite,
    MouseDelta,
    MouseScroll,
}

internal enum SourceKind
{
    None,
    Key,
    MouseButton,
    GamepadButton,
    GamepadTrigger,
    GamepadStick,
}

/// <summary>One physical input a binding reads.</summary>
internal readonly record struct Source(SourceKind Kind, int Code)
{
    /// <summary>What this source gives right now: 1 for a held key or button, a trigger's pull, 0 otherwise.</summary>
    public float Read(Input input) => Kind switch
    {
        SourceKind.Key => input.Down((Key)Code) ? 1f : 0f,
        SourceKind.MouseButton => input.Down((MouseButton)Code) ? 1f : 0f,
        SourceKind.GamepadButton => input.Down((GamepadButton)Code) ? 1f : 0f,
        SourceKind.GamepadTrigger => input.Trigger((GamepadTrigger)Code),
        _ => 0f,
    };

    /// <summary>Whether a key or button source went down since the last tick.</summary>
    public bool ReadPressed(Input input) => Kind switch
    {
        SourceKind.Key => input.Pressed((Key)Code),
        SourceKind.MouseButton => input.Pressed((MouseButton)Code),
        SourceKind.GamepadButton => input.Pressed((GamepadButton)Code),
        _ => false,
    };

    public Vector2 ReadStick(Input input) => input.Stick((GamepadStick)Code);
}
