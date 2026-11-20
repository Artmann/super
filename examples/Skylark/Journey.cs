namespace SkylarkExample;

/// <summary>
/// How the whole run is going: which level the player is on and how long they've taken. What belongs to one level,
/// such as the gems still to find, lives in the world, so it starts afresh whenever a level is built.
/// </summary>
public sealed class Journey
{
    /// <summary>The level being played, counted from 0.</summary>
    public int Level { get; set; }

    /// <summary>Seconds played since the run began.</summary>
    public float Seconds { get; set; }

    /// <summary>How many times the bird fell off.</summary>
    public int Falls { get; set; }

    /// <summary>Back to the start of a run.</summary>
    public void Reset()
    {
        Level = 0;
        Seconds = 0f;
        Falls = 0;
    }
}

/// <summary>What's kept between runs, in the player's own folder.</summary>
public sealed class Records
{
    /// <summary>The fastest finished run in seconds, or 0 before the first.</summary>
    public float BestSeconds { get; set; }
}
