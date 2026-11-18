namespace Super.Cook;

/// <summary>
/// The results of a cooker's slow steps that the cache keeps between cooks, from <see cref="CookInput.StepResults"/>.
/// Without a cache, nothing is kept and nothing is found. Safe to use from several threads.
/// </summary>
public sealed class StepResults
{
    private readonly StepCache? _steps;

    internal StepResults(StepCache? steps)
    {
        _steps = steps;
    }

    /// <summary>Whether <see cref="Recall"/> and <see cref="Keep"/> have a cache behind them.</summary>
    public bool HasCache => _steps is not null;

    /// <summary>
    /// What an earlier cook kept for <paramref name="key"/> of a slow step with <see cref="Keep"/>, or null when
    /// there's nothing, the cook has no cache, or the entry was damaged. The key must name everything the step's result
    /// depends on, its code's version included, so a change to any of them misses.
    /// </summary>
    /// <param name="step">The step's name, in letters, digits and dashes, such as <c>graph-run</c>.</param>
    public byte[]? Recall(string step, string key) => _steps?.Find(step, key);

    /// <summary>Keeps a slow step's result for <see cref="Recall"/> in later cooks. Does nothing when the cook has no cache.</summary>
    public void Keep(string step, string key, byte[] bytes) => _steps?.Keep(step, key, bytes);

    /// <summary>
    /// Like <see cref="Recall"/>, for a step whose result is an object of the cook, kept with <see cref="KeepObject"/>:
    /// the object's hash and its bytes, checked against the hash, or null when it isn't kept or the object is gone.
    /// </summary>
    internal (string Hash, byte[] Bytes)? RecallObject(string step, string key) => _steps?.FindObject(step, key);

    /// <summary>
    /// Like <see cref="Keep"/>, for a step whose result is an object the cook writes anyway, such as a shared texture: the
    /// cache keeps the object once, among its objects, and the step's entry only names it.
    /// </summary>
    /// <param name="hash">The SHA-256 of <paramref name="bytes"/> in lowercase hex.</param>
    internal void KeepObject(string step, string key, string hash, byte[] bytes) => _steps?.KeepObject(step, key, hash, bytes);
}
