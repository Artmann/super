namespace Super;

/// <summary>
/// Which frame a view is drawing, and the game time it shows: the tick, how long a tick lasts, the time scale, and how
/// far the display is between the last tick and the next.
/// </summary>
internal readonly record struct ViewClock(long FrameCount, long Tick, float TickSeconds, float TimeScale, float Blend)
{
    /// <summary>The time on screen, which sits between the last tick and the next.</summary>
    public double Now => (Tick - 1 + (double)Blend) * TickSeconds;

    /// <summary>How many seconds a pose shows past the last tick, so clips keep moving between ticks.</summary>
    public float PoseAhead => Blend * TickSeconds * TimeScale;

    /// <summary>How long ago a tick was, in seconds on screen, and never less than 0.</summary>
    public float SecondsSince(long tick) => MathF.Max((Tick - 1 - tick + Blend) * TickSeconds, 0f);

    /// <summary>The clock for the next frame, showing this time.</summary>
    public ViewClock Next(float blend, long tick, float tickSeconds, float timeScale) =>
        new(FrameCount + 1, tick, tickSeconds, timeScale, blend);
}
