namespace Super;

public sealed class GameOptions
{
    public string Title { get; init; } = "Super";
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;

    /// <summary>How many times per second Update runs.</summary>
    public int TicksPerSecond { get; init; } = 30;

    /// <summary>Picks the random numbers. Left empty, each run gets a new seed and prints it.</summary>
    public int? Seed { get; init; }
}
