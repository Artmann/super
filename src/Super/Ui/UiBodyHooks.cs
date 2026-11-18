namespace Super.Ui;

/// <summary>
/// What a component keeps between runs of its Body: its states, in the order Body asks
/// for them, and the states it read last run. When one of those changes, the component
/// is told, so it runs again on the next update.
/// </summary>
internal sealed class UiBodyHooks(Action changed)
{
    [ThreadStatic]
    private static UiBodyHooks? _running;

    private readonly List<object> _slots = [];
    private readonly List<Action> _stopReading = [];
    private int _nextSlot;
    private Type? _component;

    /// <summary>The hooks of the component whose Body is running, which is who reads of state belong to.</summary>
    public static UiBodyHooks? Running => _running;

    /// <summary>
    /// Runs a component's Body with these hooks. What it read last run is forgotten first,
    /// so only what this run reads counts.
    /// </summary>
    public TView Run<TView>(TView component, Func<TView, TView> body)
        where TView : notnull
    {
        StopReading();
        _nextSlot = 0;
        _component = component.GetType();
        var outer = _running;
        _running = this;
        try
        {
            return body(component);
        }
        finally
        {
            _running = outer;
        }
    }

    /// <summary>The next slot Body asks for, made from the initial value on the first run.</summary>
    public TSlot UseSlot<TValue, TSlot>(TValue initial, Func<TValue, TSlot> make)
        where TSlot : class
    {
        if (_nextSlot == _slots.Count)
        {
            _slots.Add(make(initial));
        }

        if (_slots[_nextSlot++] is not TSlot slot)
        {
            throw new InvalidOperationException(
                $"{_component!.Name} called UseState in a different order than last time. Call it the same way on every run of Body.");
        }

        return slot;
    }

    /// <summary>Remembers how to stop reading a state, for when Body runs again or the component goes.</summary>
    public void Reading(Action stop) => _stopReading.Add(stop);

    /// <summary>Tells the component that a state it read has changed.</summary>
    public void Changed() => changed();

    public void StopReading()
    {
        foreach (var stop in _stopReading)
        {
            stop();
        }

        _stopReading.Clear();
    }
}
