namespace Super;

/// <summary>
/// Which part of the game is running: a menu, a loading screen, the house, outside. Name
/// them with your own enum and inject <c>States&lt;YourEnum&gt;</c>; no registration needed.
/// Nothing is deleted by a change on its own, so whatever isn't tied to a state simply
/// carries through. A game starts in the enum's first value unless Load sets another.
/// </summary>
public sealed class States<TState> : IStateMachine
    where TState : struct, Enum
{
    private static readonly EqualityComparer<TState> Same = EqualityComparer<TState>.Default;

    private readonly StateChanges _changes;
    private readonly StateCallbacks<TState> _enter = new();
    private readonly StateCallbacks<TState> _exit = new();

    // What the next change does, and the state it goes to. See "Lifecycle state in engine code" in docs/design.md.
    private Pending _pending = Pending.Start;
    private TState _next;

    public States(StateChanges changes)
    {
        _changes = changes;
        changes.Add(this);
    }

    private enum Pending
    {
        /// <summary>Nothing has been entered yet. The first change enters <see cref="_next"/> with no exit.</summary>
        Start,

        /// <summary>Nothing was asked for since the last change.</summary>
        None,

        /// <summary>Move to <see cref="_next"/>, unless that's the current state.</summary>
        Move,

        /// <summary>Leave the current state and enter <see cref="_next"/>, even when it's the same one.</summary>
        Reenter,
    }

    /// <summary>The state running now.</summary>
    public TState Current { get; private set; }

    /// <summary>The state before this one, so entering a house can put the player at the right door.</summary>
    public TState Previous { get; private set; }

    bool IStateMachine.ChangeDue => _pending != Pending.None;

    /// <summary>Whether this is the current state. Systems use it to say where they run.</summary>
    public bool Is(TState state) => Same.Equals(Current, state);

    /// <summary>
    /// Asks to move to a state. The rest of this tick finishes first, and the Draw after it
    /// shows the new state. If it's asked for twice, the last one wins, and that includes
    /// <see cref="Reenter"/>: a Set after it replaces it, so setting the current state stays put.
    /// </summary>
    public void Set(TState state)
    {
        ThrowIfClosed();
        _next = state;
        if (_pending != Pending.Start)
        {
            _pending = Pending.Move;
        }
    }

    /// <summary>
    /// Asks to leave the state and enter it again, so its exit and enter callbacks run and
    /// what belongs to it is built afresh. It lands like <see cref="Set"/>. Use it to restart
    /// a level, or after hot reload changed the code that builds the state. After a Set in
    /// the same tick, it reenters the state that was set.
    /// </summary>
    public void Reenter()
    {
        ThrowIfClosed();
        switch (_pending)
        {
            case Pending.Start:
                // Nothing has been entered, so the first change enters it once anyway.
                break;
            case Pending.None:
                _next = Current;
                _pending = Pending.Reenter;
                break;
            default:
                _pending = Pending.Reenter;
                break;
        }
    }

    /// <summary>Adds something to run on entering a state. Use it to build what the state needs.</summary>
    public void OnEnter(TState state, Action enter) => _enter.Add(state, enter);

    /// <summary>Adds something to run on leaving a state, while everything in it is still there.</summary>
    public void OnExit(TState state, Action exit) => _exit.Add(state, exit);

    void IStateMachine.Change(IStateHook[] hooks)
    {
        // Taken before any callback runs, so a callback that asks for a change queues the next one
        // instead of replacing this one.
        var pending = _pending;
        var next = _next;
        _pending = Pending.None;
        switch (pending)
        {
            case Pending.Start:
                Previous = next;
                Enter(next, hooks);
                break;
            case Pending.Move when !Same.Equals(next, Current):
            case Pending.Reenter:
                Exit(hooks);
                Enter(next, hooks);
                break;
        }
    }

    private void ThrowIfClosed()
    {
        if (!_changes.Open)
        {
            throw new InvalidOperationException("States can't change in Draw or Ui, which only describe state. Change them in Update.");
        }
    }

    private void Exit(IStateHook[] hooks)
    {
        _exit.Run(Current);
        foreach (var hook in hooks)
        {
            hook.Exited(Current);
        }

        Previous = Current;
    }

    private void Enter(TState state, IStateHook[] hooks)
    {
        Current = state;
        _enter.Run(state);
        foreach (var hook in hooks)
        {
            hook.Entered();
        }
    }
}

/// <summary>The callbacks a state machine runs for each state on one side of a change: on entering, or on leaving.</summary>
internal sealed class StateCallbacks<TState>
    where TState : struct, Enum
{
    private readonly Dictionary<TState, List<Action>> _callbacks = [];

    public void Add(TState state, Action callback)
    {
        if (!_callbacks.TryGetValue(state, out var list))
        {
            list = [];
            _callbacks[state] = list;
        }

        list.Add(callback);
    }

    /// <summary>Runs the state's callbacks in the order they were added, including any added while running.</summary>
    public void Run(TState state)
    {
        if (!_callbacks.TryGetValue(state, out var list))
        {
            return;
        }

        for (int i = 0; i < list.Count; i++)
        {
            list[i]();
        }
    }
}
