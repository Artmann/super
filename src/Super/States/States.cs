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
    private readonly Dictionary<TState, List<Action>> _enter = [];
    private readonly Dictionary<TState, List<Action>> _exit = [];
    private bool _started;
    private bool _hasNext;
    private TState _next;

    public States(StateChanges changes)
    {
        _changes = changes;
        changes.Add(this);
    }

    /// <summary>The state running now.</summary>
    public TState Current { get; private set; }

    /// <summary>The state before this one, so entering a house can put the player at the right door.</summary>
    public TState Previous { get; private set; }

    bool IStateMachine.ChangeDue => !_started || _hasNext;

    /// <summary>Whether this is the current state. Systems use it to say where they run.</summary>
    public bool Is(TState state) => Same.Equals(Current, state);

    /// <summary>
    /// Asks to move to a state. The rest of this tick finishes first, and the Draw after it
    /// shows the new state. If it's asked for twice, the last one wins.
    /// </summary>
    public void Set(TState state)
    {
        if (!_changes.Open)
        {
            throw new InvalidOperationException("States can't change in Draw or Ui, which only describe state. Change them in Update.");
        }

        _next = state;
        _hasNext = true;
    }

    /// <summary>Adds something to run on entering a state. Use it to build what the state needs.</summary>
    public void OnEnter(TState state, Action enter) => Add(_enter, state, enter);

    /// <summary>Adds something to run on leaving a state, while everything in it is still there.</summary>
    public void OnExit(TState state, Action exit) => Add(_exit, state, exit);

    void IStateMachine.Change(IStateHook[] hooks)
    {
        var next = _hasNext ? _next : Current;
        _hasNext = false;
        if (!_started)
        {
            _started = true;
            Previous = next;
            Enter(next, hooks);
            return;
        }

        if (Same.Equals(next, Current))
        {
            return;
        }

        Run(_exit, Current);
        foreach (var hook in hooks)
        {
            hook.Exited(Current);
        }

        Previous = Current;
        Enter(next, hooks);
    }

    private void Enter(TState state, IStateHook[] hooks)
    {
        Current = state;
        Run(_enter, state);
        foreach (var hook in hooks)
        {
            hook.Entered();
        }
    }

    private static void Add(Dictionary<TState, List<Action>> callbacks, TState state, Action callback)
    {
        if (!callbacks.TryGetValue(state, out var list))
        {
            list = [];
            callbacks[state] = list;
        }

        list.Add(callback);
    }

    private static void Run(Dictionary<TState, List<Action>> callbacks, TState state)
    {
        if (!callbacks.TryGetValue(state, out var list))
        {
            return;
        }

        for (int i = 0; i < list.Count; i++)
        {
            list[i]();
        }
    }
}
