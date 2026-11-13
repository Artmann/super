namespace Rendition;

/// <summary>
/// The bakes in a game's content, read through a function so the renderer needn't know where content lives. Each
/// file's header is read the first time it is asked about and kept until <see cref="Forget()"/>, so asking every frame
/// reads nothing and, once each file has been asked about, allocates nothing. A file that can't be read, whether
/// damaged or failing to open, is lit live and logged once. Its header cache isn't thread-safe, so ask
/// <see cref="IsFresh"/>, <see cref="Saved"/>, <see cref="SavedQuality"/> and <see cref="Forget()"/> from the render thread only. <see cref="Load"/>
/// keeps nothing, so it may be called from another thread as long as the read function can be.
/// See "Baked lighting" in docs/rendering.md.
/// </summary>
public sealed class LightBakeStore : IBakeStore
{
    private readonly Func<string, byte[]?> _read;
    private readonly TextWriter _log;

    // Each file's saved inputs and quality, or null when it is missing or can't be read.
    private readonly Dictionary<string, (BakeInputs Inputs, BakeQuality Quality)?> _headers = [];

    // The stale files already logged, so each is logged once until the content reloads.
    private readonly HashSet<string> _logged = [];

    // The files given to Forget(string) that the renderer hasn't let go of yet.
    private readonly HashSet<string> _forgotten = [];

    /// <param name="read">Gives a file's bytes, or null when there is no such file.</param>
    /// <param name="log">Where a stale or damaged bake is reported, once per file.</param>
    public LightBakeStore(Func<string, byte[]?> read, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(log);
        _read = read;
        _log = log;
    }

    /// <summary>
    /// Whether <paramref name="file"/> exists and was made from exactly <paramref name="inputs"/>. A stale bake is
    /// logged once, naming what changed; a missing one is not fresh and isn't logged, since most areas start unbaked.
    /// </summary>
    public bool IsFresh(string file, BakeInputs inputs)
    {
        if (Saved(file) is not { } saved)
        {
            return false;
        }

        if (saved == inputs)
        {
            return true;
        }

        if (_logged.Add(file))
        {
            _log.WriteLine(
                $"The bake {file} is stale, since its {Joined(inputs.Changes(saved))} changed, so its area is lit live until it is baked again.");
        }

        return false;
    }

    /// <summary>Whether <paramref name="file"/> exists and was made from exactly <paramref name="inputs"/>, logging nothing if not.</summary>
    public bool IsFreshQuietly(string file, BakeInputs inputs) => Saved(file) is { } saved && saved == inputs;

    /// <summary>
    /// The inputs <paramref name="file"/> was made from, read from its header, or null when it is missing or its header
    /// can't be read. A damaged header is logged once.
    /// </summary>
    public BakeInputs? Saved(string file) => Header(file)?.Inputs;

    /// <summary>
    /// The quality <paramref name="file"/> was baked at, read from its header, or null when it is missing or its header
    /// can't be read.
    /// </summary>
    public BakeQuality? SavedQuality(string file) => Header(file)?.Quality;

    private (BakeInputs Inputs, BakeQuality Quality)? Header(string file)
    {
        if (_headers.TryGetValue(file, out var known))
        {
            return known;
        }

        // The read gives the whole file, though only the header is wanted. That happens once per file, until Forget.
        (BakeInputs Inputs, BakeQuality Quality)? saved = null;
        try
        {
            if (_read(file) is { } bytes)
            {
                saved = LightBakeFile.ReadInputsAndQuality(bytes);
            }
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            // Kept as unreadable until Forget, so it is logged once and tried again after the content reloads, by
            // when a cook that was replacing the file has finished.
            _log.WriteLine($"The bake {file} can't be read, so its area is lit live: {e.Message}");
        }

        _headers[file] = saved;
        return saved;
    }

    /// <summary>Reads the whole of <paramref name="file"/>, or null when it is missing. Not kept.</summary>
    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    /// <exception cref="IOException">The read failed; whatever the read function throws passes through.</exception>
    public LightBakeFile? Load(string file) => _read(file) is { } bytes ? LightBakeFile.Read(bytes) : null;

    /// <summary>How many times <see cref="Forget()"/> has been called, so the renderer reads the bakes it holds again.</summary>
    public int Reloads { get; private set; }

    /// <summary>
    /// Drops every header read and every stale file logged, so the next question reads the files again, and the
    /// renderer reads the bakes it draws again too. Call it after the content reloads.
    /// </summary>
    public void Forget()
    {
        _headers.Clear();
        _logged.Clear();
        Reloads++;
    }

    /// <summary>
    /// Drops one file's header and its stale or unreadable report, so the next question reads it again. Unlike
    /// <see cref="Forget()"/> it doesn't raise <see cref="Reloads"/>, which would make the renderer let go of every
    /// chunk bake on the GPU and read them all again. Instead the file is remembered for <see cref="TakeForgotten"/>,
    /// so the renderer lets go of only this file's bake, and stops counting it unreadable, in its next frame, without
    /// reading a file or waiting for the GPU there. Call it from the render thread only, as <see cref="Forget()"/>;
    /// a bake written by another thread is forgotten from the render thread once it is done.
    /// </summary>
    public void Forget(string file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _headers.Remove(file);
        _logged.Remove(file);
        lock (_forgotten)
        {
            _forgotten.Add(file);
        }
    }

    /// <summary>
    /// The files given to <see cref="Forget(string)"/> since the last call, each once, or none. The renderer calls it
    /// each frame, so nothing is allocated while nothing was forgotten.
    /// </summary>
    public IReadOnlyCollection<string> TakeForgotten()
    {
        lock (_forgotten)
        {
            if (_forgotten.Count == 0)
            {
                return [];
            }

            string[] taken = [.. _forgotten];
            _forgotten.Clear();
            return taken;
        }
    }

    // "draws", "draws and lights", "draws, lights and environment".
    private static string Joined(IReadOnlyList<string> changes) => changes.Count switch
    {
        0 => "nothing",
        1 => changes[0],
        _ => $"{string.Join(", ", changes.Take(changes.Count - 1))} and {changes[^1]}",
    };
}
