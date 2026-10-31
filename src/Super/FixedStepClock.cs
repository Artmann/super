namespace Super;

/// <summary>
/// Turns real time into a whole number of ticks, keeping the remainder for next time.
/// This is what lets the display run faster or slower than the simulation.
/// </summary>
internal sealed class FixedStepClock(int ticksPerSecond)
{
    /// <summary>
    /// After a stall, such as dragging the window, the game loses this much time at most.
    /// Without a limit, a slow game would fall further behind trying to catch up.
    /// </summary>
    private const double MaxCatchUpSeconds = 0.25;

    private readonly double _tickSeconds = 1.0 / ticksPerSecond;
    private double _waiting;

    /// <summary>How far the display is between the last two ticks, from 0 to 1.</summary>
    public float Blend => Math.Clamp((float)(_waiting / _tickSeconds), 0f, 1f);

    /// <summary>Adds the time since the last call and returns how many ticks are due.</summary>
    public int Advance(double elapsedSeconds)
    {
        _waiting += Math.Min(elapsedSeconds, MaxCatchUpSeconds);
        int ticks = (int)(_waiting / _tickSeconds);
        _waiting -= ticks * _tickSeconds;
        return ticks;
    }
}
