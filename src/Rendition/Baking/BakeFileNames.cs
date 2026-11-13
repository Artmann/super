namespace Rendition;

/// <summary>
/// The bake file names of spaces and chunks, worked out once for each distinct key, since building one allocates a
/// string. Part of <see cref="BakeResolver"/>.
/// </summary>
internal sealed class BakeFileNames
{
    // The file names kept at most, so a game whose chunks keep changing doesn't grow the cache for ever.
    private const int MaxCachedNames = 1024;

    private readonly Dictionary<string, string> _spaces = [];
    private readonly Dictionary<(string Name, ulong Hash), string> _chunks = [];

    /// <summary>The file of the space named <paramref name="name"/>, as <see cref="BakeFiles.Space"/> names it.</summary>
    public string Space(string name)
    {
        if (!_spaces.TryGetValue(name, out var file))
        {
            if (_spaces.Count >= MaxCachedNames)
            {
                _spaces.Clear();
            }

            file = _spaces[name] = BakeFiles.Space(name);
        }

        return file;
    }

    /// <summary>The file of chunk <paramref name="name"/> made from <paramref name="hash"/>, as <see cref="BakeFiles.Chunk"/> names it.</summary>
    public string Chunk(string name, ulong hash)
    {
        if (!_chunks.TryGetValue((name, hash), out var file))
        {
            if (_chunks.Count >= MaxCachedNames)
            {
                _chunks.Clear();
            }

            file = _chunks[(name, hash)] = BakeFiles.Chunk(name, hash);
        }

        return file;
    }
}
