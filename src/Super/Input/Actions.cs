using System.Numerics;
using System.Runtime.CompilerServices;

namespace Super;

/// <summary>
/// The things a player can do, named by your own enum and bound to any number of keys,
/// buttons, sticks and mouse motions. Game code asks about actions, never keys, so
/// rebinding touches nothing else. Inject <c>Actions&lt;YourEnum&gt;</c>; no registration needed.
/// </summary>
public sealed class Actions<TControl> : IInputListener
    where TControl : struct, Enum
{
    /// <summary>An action counts as down once its value passes this.</summary>
    private const float DownThreshold = 0.5f;

    private readonly Input _input;
    private readonly List<Binding>[] _bindings;
    private readonly State[] _states;

    public Actions(Input input)
    {
        if (Unsafe.SizeOf<TControl>() != sizeof(int))
        {
            throw new ArgumentException($"{typeof(TControl).Name} must be an int-sized enum to be used with Actions.", nameof(TControl));
        }

        int count = 0;
        foreach (var control in Enum.GetValues<TControl>())
        {
            count = Math.Max(count, Index(control) + 1);
        }

        _input = input;
        _bindings = new List<Binding>[count];
        _states = new State[count];
        for (int i = 0; i < count; i++)
        {
            _bindings[i] = [];
        }

        input.Listen(this);
    }

    /// <summary>Adds ways to perform an action. Call it in Load; bindings are kept as plain data.</summary>
    public void Bind(TControl control, params Binding[] bindings) => _bindings[Index(control)].AddRange(bindings);

    public void Unbind(TControl control) => _bindings[Index(control)].Clear();

    public IReadOnlyList<Binding> Bindings(TControl control) => _bindings[Index(control)];

    /// <summary>Whether the action is being performed this tick.</summary>
    public bool Down(TControl control) => Read(control).Down;

    /// <summary>Whether the action started since the last tick.</summary>
    public bool Pressed(TControl control) => Read(control).Pressed;

    /// <summary>Whether the action stopped since the last tick.</summary>
    public bool Released(TControl control) => Read(control).Released;

    /// <summary>How much, from 0 to 1 for keys, buttons, sticks and triggers.</summary>
    public float Value(TControl control) => Read(control).Value;

    /// <summary>Which way, with length at most 1 unless the mouse contributed.</summary>
    public Vector2 Vector(TControl control) => Read(control).Vector;

    /// <summary>Makes the action report this instead of its bindings, until cleared. Tests use it.</summary>
    internal void Override(TControl control, Vector2 vector, bool button)
    {
        ref var state = ref _states[Index(control)];
        state.HasOverride = true;
        state.OverrideVector = vector;
        state.OverrideButton = button;
    }

    internal void ClearOverride(TControl control) => _states[Index(control)].HasOverride = false;

    void IInputListener.Rolled()
    {
        for (int i = 0; i < _states.Length; i++)
        {
            ref var state = ref _states[i];
            bool wasDown = state.Down;
            bool tapped = false;
            if (state.HasOverride)
            {
                state.Vector = state.OverrideVector;
                state.Value = state.OverrideButton ? 1f : state.OverrideVector.Length();
            }
            else
            {
                Evaluate(_bindings[i], out state.Vector, out state.Value, out tapped);
            }

            // A button tapped again before its release landed is a new press, even though Down never dropped.
            state.Down = state.Value > DownThreshold;
            state.Pressed = state.Down && (!wasDown || tapped);
            state.Released = !state.Down && wasDown;
        }
    }

    private void Evaluate(List<Binding> bindings, out Vector2 vector, out float value, out bool tapped)
    {
        var clamped = Vector2.Zero;
        var free = Vector2.Zero;
        bool button = false;
        tapped = false;
        foreach (var binding in bindings)
        {
            if (binding.Kind == BindingKind.Button)
            {
                button |= binding.First.Read(_input) > 0f;
                tapped |= binding.First.ReadPressed(_input);
            }
            else
            {
                AddMotion(binding, _input, ref clamped, ref free);
            }
        }

        if (clamped.LengthSquared() > 1f)
        {
            clamped = Vector2.Normalize(clamped);
        }

        vector = clamped + free;
        float length = vector.Length();
        value = button ? Math.Max(1f, length) : length;
    }

    /// <summary>
    /// Adds what a stick, trigger, axis, composite or mouse binding gives. Sticks, triggers,
    /// axes and composites go into <paramref name="clamped"/>, which is held to length 1 after
    /// all bindings are in. The mouse goes into <paramref name="free"/>, which never is.
    /// </summary>
    private static void AddMotion(Binding binding, Input input, ref Vector2 clamped, ref Vector2 free)
    {
        switch (binding.Kind)
        {
            case BindingKind.Trigger:
                clamped.X += binding.First.Read(input) * binding.Scale;
                break;
            case BindingKind.Stick:
                clamped += binding.First.ReadStick(input) * binding.Scale;
                break;
            case BindingKind.Axis:
                clamped.X += (binding.Second.Read(input) - binding.First.Read(input)) * binding.Scale;
                break;
            case BindingKind.Composite:
                var direction = new Vector2(
                    binding.Fourth.Read(input) - binding.Third.Read(input),
                    binding.First.Read(input) - binding.Second.Read(input));
                if (direction.LengthSquared() > 1f)
                {
                    direction = Vector2.Normalize(direction);
                }

                clamped += direction * binding.Scale;
                break;
            case BindingKind.MouseDelta:
                free += input.Mouse.Delta * binding.Scale;
                break;
            case BindingKind.MouseScroll:
                free.X += input.Mouse.Scroll * binding.Scale;
                break;
        }
    }

    private ref State Read(TControl control)
    {
        _input.CheckReadable();
        return ref _states[Index(control)];
    }

    private static int Index(TControl control) => Unsafe.As<TControl, int>(ref control);

    private struct State
    {
        public Vector2 Vector;
        public float Value;
        public bool Down;
        public bool Pressed;
        public bool Released;
        public bool HasOverride;
        public Vector2 OverrideVector;
        public bool OverrideButton;
    }
}
