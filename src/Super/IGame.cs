namespace Super;

/// <summary>
/// A game. Super creates it through dependency injection, so ask for what you need in
/// the constructor.
/// </summary>
public interface IGame
{
    /// <summary>Runs once before the first tick, to build the starting world.</summary>
    void Load()
    {
    }

    /// <summary>Runs a fixed number of times per second. This is where game state changes.</summary>
    void Update(GameTime time);

    /// <summary>Runs once per displayed frame. It describes what's on screen and only reads state.</summary>
    void Draw(View view);
}
