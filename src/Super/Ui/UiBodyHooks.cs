namespace Super.Ui;

/// <summary>
/// What a component keeps between runs of its Body: its states, in the order Body asks
/// for them, and the states it read last run. When one of those changes, the component
/// is told, so it runs again on the next update.
/// </summary>
internal sealed class UiBodyHooks(Action changed, UiClock clock)
{
    [ThreadStatic]
    private static UiBodyHooks? _running;

    private readonly List<object> _slots = [];
    private readonly List<Action> _stopReading = [];
    private int _nextSlot;
    private Type? _component;

    /// <summary>The hooks of the component whose Body is running, which is who reads of state belong to.</summary>
    public static UiBodyHooks? Running => _running;

    /// <summary>What the component sits in, as it was for this run of Body.</summary>
    public UiEnvironment Environment { get; private set; } = UiEnvironment.Default;

    /// <summary>
    /// Runs a component's Body with these hooks. What it read last run is forgotten first,
    /// so only what this run reads counts.
    /// </summary>
    public TView Run<TView>(TView component, UiEnvironment environment, Func<TView, TView> body)
        where TView : notnull
    {
        StopReading();
        Environment = environment;
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

    /// <summary>
    /// The next animation slot's value at this frame's time, heading for <paramref name="target"/>. A new one
    /// rests at <paramref name="from"/>, or at the target, so one from elsewhere sets off on its first run.
    /// While it's on its way, Body runs again next frame.
    /// </summary>
    public float Animate(float target, float seconds, Easing easing, float? from)
    {
        var animation = UseSlot(from ?? target, static value => new UiAnimation(value));
        double now = clock.Now;
        if (animation.To != target || Environment.Still)
        {
            animation.Retarget(target, now, Environment.Still ? 0f : seconds, easing);
        }

        if (!animation.Arrived(now))
        {
            clock.NextFrame(this);
        }

        return animation.At(now);
    }

    /// <summary>The interface's time in seconds. Body runs again every frame while it reads it, unless motion is off.</summary>
    public double Time()
    {
        if (Environment.Still)
        {
            return 0.0;
        }

        clock.NextFrame(this);
        return clock.Now;
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
