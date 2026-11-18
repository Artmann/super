namespace Rendition;

/// <summary>
/// The chunks of a <see cref="BakeFrame"/>, in the frame's order: each one's inputs, its bake's file, how many static
/// draws it owns, and where its bake is this frame. The resolver fills it; the frame's chunk members read it.
/// </summary>
internal sealed class FrameChunkBakes
{
    private readonly List<BakeInputs> _inputs = [];
    private readonly List<bool> _fresh = [];
    private readonly List<bool> _pending = [];
    private readonly List<string> _files = [];
    private readonly List<int> _staticDraws = [];
    private readonly List<float> _fades = [];

    /// <summary>Each chunk's inputs.</summary>
    public IReadOnlyList<BakeInputs> Inputs => _inputs;

    /// <summary>Whether each chunk's bake is fresh and all on the GPU.</summary>
    public IReadOnlyList<bool> Fresh => _fresh;

    /// <summary>The inputs of chunk <paramref name="chunk"/>.</summary>
    public BakeInputs InputsOf(int chunk) => _inputs[chunk];

    /// <summary>Whether the chunk's bake is fresh in the store but not yet all on the GPU.</summary>
    public bool IsPending(int chunk) => _pending[chunk];

    /// <summary>How far the chunk's bake has faded in, 0 when it isn't fresh.</summary>
    public float FadeOf(int chunk) => _fades[chunk];

    /// <summary>The file the chunk's bake is in.</summary>
    public string FileOf(int chunk) => _files[chunk];

    /// <summary>How many of the frame's static draws the chunk owns.</summary>
    public int StaticDraws(int chunk) => _staticDraws[chunk];

    /// <summary>Adds the next chunk.</summary>
    public void Add(BakeInputs inputs, string file, int staticDraws, ChunkBakeState state)
    {
        var (fresh, pending, fade) = state;
        _inputs.Add(inputs);
        _fresh.Add(fresh);
        _pending.Add(pending);
        _files.Add(file);
        _staticDraws.Add(staticDraws);
        _fades.Add(fresh ? fade : 0f);
    }

    /// <summary>Empties the list for the next frame, keeping what it allocated.</summary>
    public void Clear()
    {
        _inputs.Clear();
        _fresh.Clear();
        _pending.Clear();
        _files.Clear();
        _staticDraws.Clear();
        _fades.Clear();
    }
}
