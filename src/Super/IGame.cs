using Super.Ui;

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

    /// <summary>
    /// The interface on top of the picture. It's read after every Draw, and Super works out
    /// what changed, so describe all of it each time. It only reads state, like Draw.
    /// </summary>
    UiView? Ui => null;

    /// <summary>
    /// Runs at the start of the next tick after hot reload changed the game's code, before
    /// Update. Code that already ran, such as Load or a state's enter callbacks, doesn't run
    /// again by itself; call <c>states.Reenter()</c> here to rebuild the current state.
    /// </summary>
    void CodeReloaded()
    {
    }

    /// <summary>Input to play by name with <c>--script</c>, read once after <see cref="Load"/>.</summary>
    IReadOnlyDictionary<string, InputScript> Scripts => new Dictionary<string, InputScript>();
}
