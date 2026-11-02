using System.Runtime.CompilerServices;

namespace Super;

/// <summary>
/// Describes how a kind of character animates: a looping clip or blend for each base
/// state, such as idle, walk or sit, and a one-shot clip for each action, such as a
/// wave or a shot, which plays over the state on some or all of the joints. Build one
/// per character kind at load, then <see cref="Start"/> an <see cref="Animator"/> per
/// character.
/// </summary>
public sealed class AnimationController<TState, TAction> : AnimationControllerBase
    where TState : struct, Enum
    where TAction : struct, Enum
{
    public AnimationController(Rig rig)
        : base(rig, typeof(TState), typeof(TAction))
    {
    }

    /// <summary>A state that loops one clip. Speed scales its pace.</summary>
    public AnimationController<TState, TAction> State(TState state, ClipRef clip, float speed = 1f)
    {
        Rig.Check(clip);
        return State(state, new Motion(Rig, [(clip.Index, 0f)]), speed);
    }

    /// <summary>A state that loops a blend from <see cref="Rig.Blend"/>, picked by <see cref="Animator.Parameter"/>.</summary>
    public AnimationController<TState, TAction> State(TState state, Motion motion, float speed = 1f)
    {
        if (motion.Rig != Rig)
        {
            throw new ArgumentException("The motion is for another rig.", nameof(motion));
        }

        CheckSpeed(speed);
        AddState(Index(state), new StateDefinition(motion, speed));
        return this;
    }

    /// <summary>
    /// An action that plays its clip once over the state. A mask keeps it to some joints,
    /// so a wave on the arms leaves the legs walking. It fades in and out over
    /// <paramref name="fadeSeconds"/>.
    /// </summary>
    public AnimationController<TState, TAction> Action(TAction action, ClipRef clip, BoneMask? mask = null, float fadeSeconds = 0.1f, float speed = 1f)
    {
        Rig.Check(clip);
        if (mask is not null && mask.Rig != Rig)
        {
            throw new ArgumentException("The mask is for another rig.", nameof(mask));
        }

        if (!(fadeSeconds >= 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(fadeSeconds), fadeSeconds, "The fade can't be negative.");
        }

        CheckSpeed(speed);
        AddAction(Index(action), new ActionDefinition(clip.Index, mask, fadeSeconds, speed));
        return this;
    }

    /// <summary>An animator for one character, in this state.</summary>
    public Animator Start(TState state) => new(this, Index(state));

    private static int Index(TState state) => Unsafe.As<TState, int>(ref state);

    private static int Index(TAction action) => Unsafe.As<TAction, int>(ref action);

    private static void CheckSpeed(float speed)
    {
        if (!(speed > 0f))
        {
            throw new ArgumentOutOfRangeException(nameof(speed), speed, "The speed must be more than zero.");
        }
    }
}

/// <summary>The part of a controller that doesn't depend on the game's enums, so an <see cref="Animator"/> needn't either.</summary>
public abstract class AnimationControllerBase
{
    private readonly Dictionary<int, StateDefinition> _states = [];
    private readonly Dictionary<int, ActionDefinition> _actions = [];

    private protected AnimationControllerBase(Rig rig, Type stateType, Type actionType)
    {
        Rig = rig;
        StateType = stateType;
        ActionType = actionType;
    }

    public Rig Rig { get; }

    /// <summary>How long a change of state takes to blend from the old clip to the new, in seconds.</summary>
    public float CrossFadeSeconds { get; set; } = 0.2f;

    internal Type StateType { get; }

    internal Type ActionType { get; }

    internal int ActionCount => _actions.Count == 0 ? 0 : _actions.Keys.Max() + 1;

    internal StateDefinition GetState(int state) =>
        _states.TryGetValue(state, out var definition)
            ? definition
            : throw new InvalidOperationException($"The controller has no clip for the state {Enum.ToObject(StateType, state)}.");

    internal ActionDefinition GetAction(int action) =>
        _actions.TryGetValue(action, out var definition)
            ? definition
            : throw new InvalidOperationException($"The controller has no clip for the action {Enum.ToObject(ActionType, action)}.");

    internal bool HasAction(int action) => _actions.ContainsKey(action);

    private protected void AddState(int state, StateDefinition definition) => _states[state] = definition;

    private protected void AddAction(int action, ActionDefinition definition)
    {
        if (action < 0)
        {
            throw new ArgumentException("Action enum values must be 0 or more.", nameof(action));
        }

        _actions[action] = definition;
    }
}

internal sealed record StateDefinition(Motion Motion, float Speed);

internal sealed record ActionDefinition(int Clip, BoneMask? Mask, float FadeSeconds, float Speed);
