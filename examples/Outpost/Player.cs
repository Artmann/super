namespace OutpostExample;

/// <summary>What the player has: gold to build with, lives to lose, and the enemies their towers have killed.</summary>
public sealed class Player
{
    public const int StartingGold = 100;
    public const int StartingLives = 10;

    public int Gold { get; set; } = StartingGold;

    public int Lives { get; set; } = StartingLives;

    public int Killed { get; set; }

    /// <summary>Back to how a new game starts.</summary>
    public void Reset()
    {
        Gold = StartingGold;
        Lives = StartingLives;
        Killed = 0;
    }
}
