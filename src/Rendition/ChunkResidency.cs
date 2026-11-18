namespace Rendition;

/// <summary>
/// Answers the resolver's question for a chunk whose bake is fresh in the store: is every page of it on the GPU? Keeps
/// the resident chunk bakes the frame names, in the order first named. See "Chunk bakes on the GPU" in docs/design.md.
/// </summary>
internal sealed class ChunkResidency(ChunkBakePool pool, ReadableBakes readable)
{
    private readonly List<ChunkBakePool.Entry> _named = [];
    private Func<string, BakeInputs, bool>? _question;

    /// <summary>The question as the resolver's hook, made once.</summary>
    public Func<string, BakeInputs, bool> Question => _question ??= IsResident;

    /// <summary>Forgets the chunks named so far, before the frame is resolved.</summary>
    public void ClearNamed() => _named.Clear();

    /// <summary>
    /// A hash of the resident chunk bakes this frame names, 0 for none, so the reflection probes see one come or go.
    /// A file's name is its chunk's name and inputs, so its hash says which bake it is.
    /// </summary>
    public ulong NamedHash()
    {
        if (_named.Count == 0)
        {
            return 0;
        }

        ulong hash = 0;
        foreach (var chunk in _named)
        {
            hash = hash * 31 + (ulong)StringComparer.Ordinal.GetHashCode(chunk.File);
        }

        return hash == 0 ? 1 : hash;
    }

    /// <summary>
    /// A store with no Load of its own has no lightmaps to wait for, so its fresh chunks count at once. Otherwise the
    /// pool is asked, which starts reading the bake the first time, and a resident bake joins the frame's named chunks.
    /// </summary>
    private bool IsResident(string file, BakeInputs inputs)
    {
        var store = readable.Inner;
        if (!readable.GivesLightmaps(store))
        {
            return true;
        }

        if (pool.Use(file, store) is not { } entry)
        {
            return false;
        }

        if (!_named.Contains(entry))
        {
            _named.Add(entry);
        }

        return true;
    }
}
