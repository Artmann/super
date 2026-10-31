namespace Super;

/// <summary>Simulation time, counted in ticks. It never reads the system clock.</summary>
public sealed class GameTime
{
    internal GameTime(int ticksPerSecond)
    {
        if (ticksPerSecond <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticksPerSecond), ticksPerSecond, "There must be at least one tick per second.");
        }

        TicksPerSecond = ticksPerSecond;
        TickSeconds = 1f / ticksPerSecond;
    }

    /// <summary>The number of the current tick. The first Update sees 0.</summary>
    public long Tick { get; internal set; }

    public int TicksPerSecond { get; }

    /// <summary>The length of one tick in seconds.</summary>
    public float TickSeconds { get; }

    /// <summary>How long the simulation has been running, in seconds.</summary>
    public double TotalSeconds => (double)Tick / TicksPerSecond;
}
