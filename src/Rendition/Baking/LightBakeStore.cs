namespace Rendition;

/// <summary>
/// The bakes in a game's content, read through a function so the renderer needn't know where content lives. Each
/// file's header is read the first time it is asked about and kept until <see cref="Forget"/>, so asking every frame
/// reads nothing and, once each file has been asked about, allocates nothing. See "Baked lighting" in
/// docs/rendering.md.
/// </summary>
public sealed class LightBakeStore : IBakeStore
{
    private readonly Func<string, byte[]?> _read;
    private readonly TextWriter _log;

    // Each file's saved inputs, or null when it is missing or can't be read.
    private readonly Dictionary<string, BakeInputs?> _headers = [];

    // The stale files already logged, so each is logged once until the content reloads.
    private readonly HashSet<string> _logged = [];

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

    /// <summary>
    /// The inputs <paramref name="file"/> was made from, read from its header, or null when it is missing or its header
    /// can't be read. A damaged header is logged once.
    /// </summary>
    public BakeInputs? Saved(string file)
    {
        if (_headers.TryGetValue(file, out var known))
        {
            return known;
        }

        BakeInputs? saved = null;
        if (_read(file) is { } bytes)
        {
            try
            {
                saved = LightBakeFile.ReadInputs(bytes);
            }
            catch (InvalidDataException e)
            {
                _log.WriteLine($"The bake {file} can't be read, so its area is lit live: {e.Message}");
            }
        }

        _headers[file] = saved;
        return saved;
    }

    /// <summary>Reads the whole of <paramref name="file"/>, or null when it is missing. Not kept.</summary>
    /// <exception cref="InvalidDataException">The file is damaged.</exception>
    public LightBakeFile? Load(string file) => _read(file) is { } bytes ? LightBakeFile.Read(bytes) : null;

    /// <summary>Drops every header read and every stale file logged, so the next question reads the files again. Call it after the content reloads.</summary>
    public void Forget()
    {
        _headers.Clear();
        _logged.Clear();
    }

    // "draws", "draws and lights", "draws, lights and environment".
    private static string Joined(IReadOnlyList<string> changes) => changes.Count switch
    {
        0 => "nothing",
        1 => changes[0],
        _ => $"{string.Join(", ", changes.Take(changes.Count - 1))} and {changes[^1]}",
    };
}
