namespace Super;

/// <summary>
/// Something the game loop tells just before each Update. Register one as a service to
/// do bookkeeping every tick, like remembering where things were so frames can blend.
/// </summary>
public interface ITickHook
{
    /// <summary>Runs once per tick, before the game's Update.</summary>
    void BeforeUpdate();
}
