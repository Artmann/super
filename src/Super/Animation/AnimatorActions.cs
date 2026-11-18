using System.Runtime.CompilerServices;

namespace Super;

/// <summary>
/// The one-shot actions playing on an animator, such as a wave or a shot, over whatever
/// state it's in.
/// </summary>
public sealed class AnimatorActions
{
    private readonly Type _actionType;
    private readonly Func<int, ActionTiming> _timing;
    private readonly ActionSweep _sweep;
    private readonly Action _changed;
    private ActionPlay[] _plays;

    /// <param name="actionType">The controller's action enum, which every call must use.</param>
    /// <param name="count">How many actions the controller has so far.</param>
    /// <param name="timing">An action's clip, speed and length, throwing for one the controller hasn't got.</param>
    /// <param name="sweep">Told each playing action's stretch of play over a tick, to find the clip events it passed.</param>
    /// <param name="changed">Called when an action starts or stops, so the pose is built again.</param>
    internal AnimatorActions(Type actionType, int count, Func<int, ActionTiming> timing, ActionSweep sweep, Action changed)
    {
        _actionType = actionType;
        _plays = new ActionPlay[count];
        _timing = timing;
        _sweep = sweep;
        _changed = changed;
    }

    /// <summary>Each action's play, by the action's number.</summary>
    internal ReadOnlySpan<ActionPlay> Plays => _plays;

    /// <summary>Plays an action from its start, over whatever is playing. Playing it again restarts it.</summary>
    public void Play<TAction>(TAction action) where TAction : struct, Enum
    {
        int index = ActionNumber(action);
        _timing(index);
        if (index >= _plays.Length)
        {
            Array.Resize(ref _plays, index + 1);
        }

        _plays[index] = new ActionPlay(Playing: true, Time: 0f, PreviousTime: -1f);
        _changed();
    }

    /// <summary>Stops an action before its end.</summary>
    public void Stop<TAction>(TAction action) where TAction : struct, Enum
    {
        int index = ActionNumber(action);
        if (index < _plays.Length)
        {
            _plays[index] = _plays[index] with { Playing = false };
        }

        _changed();
    }

    public bool IsPlaying<TAction>(TAction action) where TAction : struct, Enum
    {
        int index = ActionNumber(action);
        return index < _plays.Length && _plays[index].Playing;
    }

    /// <summary>
    /// How far into its clip an action is, in seconds of the clip, as of the last tick, or
    /// null when it isn't playing. Between ticks the drawn pose is further on, by the view's
    /// blend times the tick, times the animator's speed and the action's own speed.
    /// </summary>
    public float? TimeOf<TAction>(TAction action) where TAction : struct, Enum
    {
        int index = ActionNumber(action);
        return index < _plays.Length && _plays[index].Playing ? _plays[index].Time : null;
    }

    /// <summary>
    /// True on the one tick an action passed this moment of its clip, in seconds, so a
    /// shot fires when the hand squeezes, not when the clip starts. Passing 0 is true
    /// the tick the action starts.
    /// </summary>
    public bool Crossed<TAction>(TAction action, float seconds) where TAction : struct, Enum
    {
        int index = ActionNumber(action);
        if (index >= _plays.Length)
        {
            return false;
        }

        ActionPlay play = _plays[index];
        return play.PreviousTime < seconds && seconds <= play.Time;
    }

    /// <summary>
    /// Moves the playing actions on by this much of the animator's time, telling the sweep
    /// what each passed. An action stops once it reaches the end of its clip.
    /// </summary>
    internal void Advance(float seconds)
    {
        for (int i = 0; i < _plays.Length; i++)
        {
            ref ActionPlay play = ref _plays[i];
            bool fresh = play.PreviousTime < 0f;
            play.PreviousTime = play.Time;
            if (!play.Playing)
            {
                continue;
            }

            ActionTiming timing = _timing(i);
            play.Time += seconds * timing.Speed;
            _sweep(i,timing.Clip, play.PreviousTime, play.Time, fresh);
            if (play.Time >= timing.Duration)
            {
                play.Playing = false;
            }
        }
    }

    private int ActionNumber<TAction>(TAction action) where TAction : struct, Enum
    {
        if (typeof(TAction) != _actionType)
        {
            throw new InvalidOperationException($"The animator's actions are {_actionType.Name}, not {typeof(TAction).Name}.");
        }

        return Unsafe.As<TAction, int>(ref action);
    }
}

/// <summary>One action's play: whether it's playing, and where in its clip it is now and was a tick ago.</summary>
internal record struct ActionPlay(bool Playing, float Time, float PreviousTime);

/// <summary>What an action plays and how: its clip, its speed and the clip's length in seconds.</summary>
internal readonly record struct ActionTiming(int Clip, float Speed, float Duration);

/// <summary>Told an action's stretch of play over a tick, from one time in its clip to another, to find the clip events it passed.</summary>
internal delegate void ActionSweep(int action, int clip, float from, float to, bool fresh);
