using System.Numerics;

namespace Super;

/// <summary>
/// Script steps that play an action from the game's <see cref="Actions{TControl}"/>, whatever it's bound to. A
/// script written with actions keeps working when the keys change, and can lean a stick: hold a move action at a
/// vector. An action held by a script reports that and not its bindings, until released.
/// </summary>
public static class InputScriptActions
{
    /// <summary>A tap: the action is performed on this tick and stops on the next. It takes a tick.</summary>
    public static InputScript Press<TControl>(this InputScript script, TControl control)
        where TControl : struct, Enum =>
        script
            .Hold(control)
            .Then(input => Of<TControl>(input).ClearOverride(control), ticksFromNow: 1)
            .Wait(1);

    /// <summary>Keeps the action going, as a held button, until <see cref="Release{TControl}"/>.</summary>
    public static InputScript Hold<TControl>(this InputScript script, TControl control)
        where TControl : struct, Enum =>
        script.Then(input => Of<TControl>(input).Override(control, Vector2.Zero, button: true));

    /// <summary>Keeps the action at this vector, as a stick held there, until <see cref="Release{TControl}"/>.</summary>
    public static InputScript Hold<TControl>(this InputScript script, TControl control, Vector2 vector)
        where TControl : struct, Enum =>
        script.Then(input => Of<TControl>(input).Override(control, vector, button: false));

    /// <summary>Hands the action back to its bindings.</summary>
    public static InputScript Release<TControl>(this InputScript script, TControl control)
        where TControl : struct, Enum =>
        script.Then(input => Of<TControl>(input).ClearOverride(control));

    // The game's actions for this enum, found when the step plays, since a script is written before the game exists.
    private static Actions<TControl> Of<TControl>(Input input)
        where TControl : struct, Enum
    {
        foreach (var listener in input.Listeners)
        {
            if (listener is Actions<TControl> actions)
            {
                return actions;
            }
        }

        throw new InvalidOperationException(
            $"A script played a {typeof(TControl).Name} action, but the game has no Actions<{typeof(TControl).Name}>. Inject one and bind it in Load.");
    }
}
