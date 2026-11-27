namespace Super;

/// <summary>
/// The game's window as the player uses it: whether it has the keyboard, and whether that changed since the last
/// tick, frozen for one tick. A run with no window has focus unless a test takes it away. Reach it through
/// <c>input.Window</c>.
/// </summary>
public sealed class WindowInput
{
    private readonly InputTick _tick;
    private bool _has = true;
    private bool _lost;
    private bool _gained;
    private bool _lostSince;
    private bool _gainedSince;

    internal WindowInput(InputTick tick) => _tick = tick;

    /// <summary>
    /// Whether the window has the keyboard on this tick. It's false while the player is in another window, which is
    /// when a game would pause or quiet itself.
    /// </summary>
    public bool HasFocus => _tick.Read(_has);

    /// <summary>Whether the window lost focus since the last tick, even if it came back before this one.</summary>
    public bool FocusLost => _tick.Read(_lost);

    /// <summary>Whether the window got focus back since the last tick, even if it went away again before this one.</summary>
    public bool FocusGained => _tick.Read(_gained);

    /// <summary>Whether the window has focus right now, between ticks, as the window and tests feed it.</summary>
    internal bool PendingFocus { get; private set; } = true;

    internal void FocusChanged(bool focused)
    {
        if (focused == PendingFocus)
        {
            return;
        }

        PendingFocus = focused;
        if (focused)
        {
            _gainedSince = true;
        }
        else
        {
            _lostSince = true;
        }
    }

    internal void Roll()
    {
        _has = PendingFocus;
        _lost = _lostSince;
        _gained = _gainedSince;
        _lostSince = false;
        _gainedSince = false;
    }
}
