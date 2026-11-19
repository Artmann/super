namespace OutpostExample;

/// <summary>Everything the player can do. Which key or button does it is set in OutpostGame.Load.</summary>
public enum Control
{
    Build,
    Start,
    Restart,
}

/// <summary>Where the game is: on the title, playing, or over.</summary>
public enum Screen
{
    Title,
    Playing,
    Won,
    Lost,
}
