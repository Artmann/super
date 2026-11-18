namespace Super;

/// <summary>
/// Whether an error in the game's code has stopped it, and when it may try again: after a change to the code or the
/// content. Only live play keeps going after an error; otherwise the error closes the game.
/// </summary>
internal sealed class LoopHalts(Input input)
{
    private int _codeChangesSeen = HotReload.Changes;
    private bool _codeChanged;

    // What stopped the game, or null while it runs. See "Lifecycle state in engine code" in docs/design.md.
    private Halt? _halt;

    /// <summary>
    /// With hot reload on, an exception in the game's code stops the game instead of closing
    /// it, so the next saved fix carries on from where it was. Only live play turns this on.
    /// </summary>
    public bool KeepsRunning { get; set; }

    /// <summary>Where a stop is reported.</summary>
    public TextWriter Errors { get; set; } = Console.Error;

    /// <summary>What stopped the game, until a change to the code or content lets it try again.</summary>
    public Exception? Stopped => _halt?.Error;

    /// <summary>Acts as if hot reload changed the code. It lands at the start of the next tick.</summary>
    public void CodeChanged() => _codeChanged = true;

    private bool CodeChangeDue => _codeChanged || HotReload.Changes != _codeChangesSeen;

    /// <summary>Whether the code changed since the last tick that asked. Each change is said once.</summary>
    public bool TakeCodeChange()
    {
        if (!CodeChangeDue)
        {
            return false;
        }

        _codeChanged = false;
        _codeChangesSeen = HotReload.Changes;
        return true;
    }

    /// <summary>
    /// Whether the next tick runs: always while the game runs, and once it's stopped, only after a change to the code
    /// or to the content (<paramref name="reloadWanted"/>), which lets it try again.
    /// </summary>
    public bool MayTick(bool reloadWanted)
    {
        if (_halt is null)
        {
            return true;
        }

        if (!CodeChangeDue && !reloadWanted)
        {
            return false;
        }

        _halt = null;
        return true;
    }

    /// <summary>Stops the game because a tick threw. Frames still draw the world, with the error over it.</summary>
    public void StopTick(Exception error) => _halt = new UpdateHalt(FirstError(error));

    /// <summary>
    /// Whether the next frame runs the game's Draw and Ui. Once either has thrown, they aren't called again until a
    /// change, so the error is reported once.
    /// </summary>
    public bool MayDraw => _halt is not DrawHalt;

    /// <summary>Stops the game because Draw or Ui threw, so frames run none of the game's code.</summary>
    public void StopDraw(Exception error) => _halt = new DrawHalt(FirstError(error));

    /// <summary>
    /// The error the game stops with: this one, reported, or the one it already stopped with, so a Draw that throws
    /// after Update stopped shows and reports only the first.
    /// </summary>
    private Exception FirstError(Exception error)
    {
        // Update may have thrown part way, with input still readable. A Draw that threw leaves state changes
        // closed, which a stopped frame opens again.
        input.Readable = false;
        if (_halt is { } halt)
        {
            return halt.Error;
        }

        Errors.WriteLine($"{StoppedPanel.Title}: {error}");
        Errors.WriteLine(StoppedPanel.CarryOn);
        return error;
    }

    /// <summary>What stopped the game, which a change to the code or content clears.</summary>
    private abstract record Halt(Exception Error);

    /// <summary>A tick threw. Frames still draw the world, with the error in place of the game's interface.</summary>
    private sealed record UpdateHalt(Exception Error) : Halt(Error);

    /// <summary>Draw or Ui threw, so frames run none of the game's code and show only the error.</summary>
    private sealed record DrawHalt(Exception Error) : Halt(Error);
}
