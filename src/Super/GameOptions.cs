namespace Super;

public sealed class GameOptions
{
    public string Title { get; init; } = "Super";

    /// <summary>
    /// Names the folder the player's <see cref="UserData"/> is kept in, such as "reef". Never
    /// change it once the game ships, or players lose their settings. Left empty, the title is used.
    /// </summary>
    public string? Id { get; init; }
    public int Width { get; init; } = 1280;
    public int Height { get; init; } = 720;

    /// <summary>
    /// How many pixels one unit of the interface covers, so 2 makes all of it twice as big.
    /// <c>--ui-scale</c> overrides it. See <see cref="Ui.UiTree.Scale"/>.
    /// </summary>
    public float UiScale { get; init; } = 1f;

    /// <summary>How many times per second Update runs.</summary>
    public int TicksPerSecond { get; init; } = 30;

    /// <summary>Picks the random numbers. Left empty, each run gets a new seed and prints it.</summary>
    public int? Seed { get; init; }
}
