namespace Super;

/// <summary>
/// Something the game loop tells after each Draw. Register one as a service to act on what a frame
/// showed, like keeping sounds where their entities were drawn.
/// </summary>
public interface IDrawHook
{
    /// <summary>
    /// Runs once per frame, after the game's Draw and after a playing cutscene has set the camera,
    /// so <paramref name="view"/> holds the frame's final camera.
    /// </summary>
    void AfterDraw(View view);
}
