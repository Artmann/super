namespace SkylarkExample;

/// <summary>How the player is doing: which level they're on, the gems they've found there, and the whole run so far.</summary>
public sealed class Journey
{
    /// <summary>The level being played, counted from 0.</summary>
    public int Level { get; set; }

    /// <summary>Gems found on this level.</summary>
    public int Gems { get; set; }

    /// <summary>Seconds played since the run began.</summary>
    public float Seconds { get; set; }

    /// <summary>How many times the bird fell off.</summary>
    public int Falls { get; set; }

    /// <summary>The bird reached the lit beacon, so the game should move on.</summary>
    public bool ReachedBeacon { get; set; }

    /// <summary>Back to the start of a run.</summary>
    public void Reset()
    {
        Level = 0;
        Seconds = 0f;
        Falls = 0;
        StartLevel();
    }

    /// <summary>Clears what belongs to one level.</summary>
    public void StartLevel()
    {
        Gems = 0;
        ReachedBeacon = false;
    }
}

/// <summary>What's kept between runs, in the player's own folder.</summary>
public sealed class Records
{
    /// <summary>The fastest finished run in seconds, or 0 before the first.</summary>
    public float BestSeconds { get; set; }
}
