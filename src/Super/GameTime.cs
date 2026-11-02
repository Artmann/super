namespace Super;

/// <summary>Simulation time, counted in ticks. It never reads the system clock.</summary>
public sealed class GameTime
{
    private float _scale = 1f;

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

    /// <summary>
    /// How fast the world moves: 1 as normal, 0.25 for slow motion, 0 to freeze it. Ticks
    /// keep coming at the same rate, so input and the camera stay smooth; only what moves
    /// by <see cref="DeltaSeconds"/> slows down. Animators from content follow it, and a
    /// <see cref="Cutscene"/> can set it.
    /// </summary>
    public float Scale
    {
        get => _scale;
        set
        {
            if (!(value >= 0f) || !float.IsFinite(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The time scale must be a number of at least zero.");
            }

            _scale = value;
        }
    }

    /// <summary>How much the world moves on this tick: the tick's length times <see cref="Scale"/>. Move things by this.</summary>
    public float DeltaSeconds => TickSeconds * _scale;
}
