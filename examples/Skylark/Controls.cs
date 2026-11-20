namespace SkylarkExample;

/// <summary>Everything the player can do. Which key, button or stick does it is set in SkylarkGame.Load.</summary>
public enum Control
{
    Move,
    Look,
    Jump,
    Start,
    Restart,
}

/// <summary>Where the game is: on the title, playing, or finished.</summary>
public enum Screen
{
    Title,
    Playing,
    Won,
}
