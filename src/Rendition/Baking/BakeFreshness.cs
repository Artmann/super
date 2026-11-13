namespace Rendition;

/// <summary>Where saved bakes are looked up. See "Baked lighting" in docs/rendering.md.</summary>
public interface IBakeStore
{
    /// <summary>
    /// Whether the bake at <paramref name="file"/>, such as <c>lighting/courtyard.lightbake</c>, exists and was made
    /// from exactly <paramref name="inputs"/>.
    /// </summary>
    bool IsFresh(string file, BakeInputs inputs);

    /// <summary>
    /// The same answer as <see cref="IsFresh"/>, but a stale bake isn't reported. The resolver asks this way until
    /// <see cref="AnimatedLights.Settled"/>, while a bake may look stale only because a moving light hasn't been seen
    /// moving yet.
    /// </summary>
    bool IsFreshQuietly(string file, BakeInputs inputs) => IsFresh(file, inputs);

    /// <summary>
    /// The quality <paramref name="file"/> was baked at, or null when it is missing or the store can't tell. A
    /// background bake whose area is fresh at a quality before the last it is asked for bakes the rest; when the store
    /// can't tell, a fresh area counts as done.
    /// </summary>
    BakeQuality? SavedQuality(string file) => null;

    /// <summary>
    /// Reads the whole of <paramref name="file"/>, for the renderer to draw its lightmaps once
    /// <see cref="IsFresh"/> has said it is fresh. A store that leaves this out has no lightmaps to give: it answers
    /// null, and the area still counts as baked, but its surfaces take their soft light live. A store that has its own
    /// Load and answers null, as when the file was removed after its header was read, is treated as having an
    /// unreadable file: the renderer reports it once and lights its area live. A chunk's bake is loaded on a worker
    /// thread while the render thread goes on asking <see cref="IsFresh"/>, so a store's Load must be safe to call
    /// alongside its other members.
    /// </summary>
    /// <exception cref="InvalidDataException">The file is damaged. The renderer then lights its area live.</exception>
    /// <exception cref="IOException">The read failed. The renderer then lights its area live.</exception>
    LightBakeFile? Load(string file) => null;

    /// <summary>
    /// Goes up each time the store's files may have changed, such as after the content reloads, so the renderer reads
    /// a bake it holds again even when its inputs are the same.
    /// </summary>
    int Reloads => 0;

    /// <summary>
    /// The files whose bake changed since the last call, each given once, such as a chunk bake written over a damaged
    /// one. The renderer, each frame, lets go of only these files' bakes and stops counting them unreadable, so they
    /// are read again, without the other chunk bakes going and being uploaded again as a change of
    /// <see cref="Reloads"/> would. It must neither read a file nor allocate while nothing changed.
    /// </summary>
    IReadOnlyCollection<string> TakeForgotten() => [];
}

/// <summary>A store with no bakes in it, so every area is lit live and every light is real-time.</summary>
public sealed class NoBakes : IBakeStore
{
    public static readonly NoBakes Instance = new();

    private NoBakes()
    {
    }

    /// <summary>Always false: there is no bake to be fresh.</summary>
    public bool IsFresh(string file, BakeInputs inputs) => false;
}

/// <summary>
/// What one frame's baked areas came to: each area's inputs, whether its bake is fresh, how each light is used, and
/// the Baked lights a fresh bake takes over, which the frame then leaves out. Made by <see cref="BakeResolver"/>, which
/// writes the next frame into the same object, so a frame is only valid until the resolver is called again.
/// </summary>
public sealed class BakeFrame
{
    /// <summary>A frame that names no baked area: nothing is hashed, nothing is fresh, and every light is real-time.</summary>
    internal static readonly BakeFrame None = new();

    private readonly Dictionary<LightKey, LightMode> _modes = [];
    private readonly List<BakeInputs> _chunks = [];
    private readonly List<bool> _chunkFresh = [];
    private readonly List<bool> _chunkPending = [];
    private readonly List<string> _chunkFiles = [];
    private readonly List<int> _chunkDraws = [];
    private readonly List<int> _droppedLocalLights = [];
    private readonly List<int> _droppedSuns = [];
    private readonly List<float> _chunkFade = [];
    private readonly List<(int Index, float Fade)> _fadingLocalLights = [];
    private readonly List<(int Index, float Fade)> _fadingSuns = [];
    private readonly List<int> _unslottedLights = [];

    internal BakeFrame()
    {
    }

    /// <summary>The space's inputs, or null when the frame names no space.</summary>
    public BakeInputs? Space { get; private set; }

    /// <summary>Each chunk's inputs, in the frame's order.</summary>
    public IReadOnlyList<BakeInputs> Chunks => _chunks;

    /// <summary>Whether the store holds a bake of the space made from <see cref="Space"/>.</summary>
    public bool SpaceFresh { get; private set; }

    /// <summary>
    /// For each chunk, whether the store holds a bake made from its inputs and the renderer has every page of it on
    /// the GPU. A chunk whose bake is fresh but still on its way is <see cref="ChunkPending"/> instead, and counts as
    /// unbaked until it arrives.
    /// </summary>
    public IReadOnlyList<bool> ChunkFresh => _chunkFresh;

    /// <summary>
    /// Whether chunk <paramref name="chunk"/>'s bake is fresh in the store but not yet all on the GPU: being read, or
    /// uploaded a little a frame. Until it arrives the chunk isn't <see cref="ChunkFresh"/>, so its Baked lights are
    /// drawn live. Always false for a frame resolved without a renderer, as by <see cref="BakeFreshness.Resolve"/>.
    /// </summary>
    public bool ChunkPending(int chunk) => _chunkPending[chunk];

    /// <summary>
    /// How far the space's bake has faded in, from 0 to 1: 1 once it is all there, and 0 for a space that isn't
    /// <see cref="SpaceFresh"/> or that names no space. A bake that arrives while its space is drawn live, such as one
    /// saved by a background bake, fades in over <see cref="Renderer.BakeFadeSeconds"/>; one fresh on the first frame
    /// that names the space is 1 at once.
    /// </summary>
    public float SpaceFade { get; private set; }

    /// <summary>
    /// How far chunk <paramref name="chunk"/>'s bake has faded in, from 0 to 1, as <see cref="SpaceFade"/> is for the
    /// space: 0 for a chunk that isn't <see cref="ChunkFresh"/>.
    /// </summary>
    public float ChunkFade(int chunk) => _chunkFade[chunk];

    // The Baked lights over a fresh bake that is still fading in, with how far it has faded: the frame keeps them,
    // their radiance scaled by 1 minus that, instead of dropping them, so the area never goes darker than either end.
    internal IReadOnlyList<(int Index, float Fade)> FadingLocalLights => _fadingLocalLights;

    internal IReadOnlyList<(int Index, float Fade)> FadingSuns => _fadingSuns;

    /// <summary>
    /// How the light is used this frame: its own mode when the area that owns it has a fresh bake and it isn't
    /// moving, and <see cref="LightMode.Realtime"/> otherwise, as for a light this frame doesn't have.
    /// </summary>
    public LightMode ModeOf(LightKey key) => _modes.TryGetValue(key, out var mode) ? mode : LightMode.Realtime;

    /// <summary>
    /// The indices, ascending, of the frame's point and spot lights that are Baked and over a fresh bake that has
    /// faded all the way in. One whose bake is still fading in stays in the frame, dimmed, though its
    /// <see cref="ModeOf"/> is already Baked.
    /// </summary>
    public IReadOnlyList<int> DroppedLocalLights => _droppedLocalLights;

    /// <summary>
    /// The indices, ascending, of the frame's suns that are Baked and over a fresh space bake that has faded all the
    /// way in.
    /// </summary>
    public IReadOnlyList<int> DroppedSuns => _droppedSuns;

    /// <summary>
    /// The <see cref="LocalLight.Key"/>, ascending, of each Mixed chunk light with a channel in its chunk's bake that
    /// found none of the frame's four shadow mask slots free: overlapping lights and the space's held them all. Such
    /// a light keeps its real-time shadow only. Filled by the renderer as it draws the frame, and empty for a frame
    /// resolved without one. See "Shadow mask channels" in docs/design.md.
    /// </summary>
    public IReadOnlyList<int> UnslottedLights => _unslottedLights;

    internal List<int> Unslotted => _unslottedLights;

    /// <summary>
    /// The file chunk <paramref name="chunk"/>'s bake is in, <see cref="BakeFiles.Chunk"/> of its name and
    /// <c>Chunks[chunk].Combined</c>. The resolver keeps the names, so asking allocates nothing once a frame has
    /// seen them.
    /// </summary>
    public string ChunkFile(int chunk) => _chunkFiles[chunk];

    /// <summary>How many of the frame's static draws chunk <paramref name="chunk"/> owns. A chunk that owns none has nothing to bake.</summary>
    public int ChunkStaticDraws(int chunk) => _chunkDraws[chunk];

    internal List<BakeInputs> ChunkInputs => _chunks;

    // Empties the frame for the resolver to fill again, keeping everything it allocated.
    internal void Reset()
    {
        Space = null;
        SpaceFresh = false;
        SpaceFade = 0f;
        _chunks.Clear();
        _chunkFresh.Clear();
        _chunkPending.Clear();
        _chunkFiles.Clear();
        _chunkDraws.Clear();
        _chunkFade.Clear();
        _modes.Clear();
        _droppedLocalLights.Clear();
        _droppedSuns.Clear();
        _fadingLocalLights.Clear();
        _fadingSuns.Clear();
        _unslottedLights.Clear();
    }

    internal void SetSpace(BakeInputs inputs, bool fresh, float fade)
    {
        Space = inputs;
        SpaceFresh = fresh;
        SpaceFade = fresh ? fade : 0f;
    }

    internal void AddChunk(BakeInputs inputs, bool fresh, bool pending, string file, int staticDraws, float fade)
    {
        _chunks.Add(inputs);
        _chunkFresh.Add(fresh);
        _chunkPending.Add(pending);
        _chunkFiles.Add(file);
        _chunkDraws.Add(staticDraws);
        _chunkFade.Add(fresh ? fade : 0f);
    }

    internal void SetMode(LightKey key, LightMode mode) => _modes[key] = mode;

    // A Baked light over a fresh bake: dropped once the bake is all there, kept dimmed while it fades in.
    internal void DropLocalLight(int index, float fade)
    {
        if (fade >= 1f)
        {
            _droppedLocalLights.Add(index);
        }
        else
        {
            _fadingLocalLights.Add((index, fade));
        }
    }

    internal void DropSun(int index, float fade)
    {
        if (fade >= 1f)
        {
            _droppedSuns.Add(index);
        }
        else
        {
            _fadingSuns.Add((index, fade));
        }
    }
}

/// <summary>
/// Works out, once a frame, which baked areas have a fresh bake and so which lights the bake takes over. See
/// "Is the bake fresh?" in docs/design.md.
/// </summary>
public static class BakeFreshness
{
    /// <summary>
    /// Resolves the frame with a new <see cref="BakeResolver"/>, so what it returns is the caller's to keep. The
    /// renderer keeps one resolver instead, which allocates nothing after its first frames. See
    /// <see cref="BakeResolver.Resolve"/> for what is hashed and decided.
    /// </summary>
    /// <remarks>
    /// A frame that names no space and no chunks returns one shared empty frame at once, without hashing anything,
    /// asking the store or updating <paramref name="animated"/>. Otherwise it allocates the resolver and the frame.
    /// </remarks>
    public static BakeFrame Resolve(
        in FrameView frame, ReadOnlySpan<DrawItem> draws, Func<DetailHandle, BakeSphere> bounds, ulong environment,
        AnimatedLights animated, IBakeStore store) =>
        new BakeResolver(store).Resolve(frame, draws, bounds, environment, animated);

    /// <summary>
    /// <paramref name="items"/> without the ones at <paramref name="dropped"/> (ascending indices), copied into
    /// <paramref name="buffer"/>, which grows when it must and is kept for the next frame. With nothing dropped it is
    /// <paramref name="items"/> itself, copied nowhere.
    /// </summary>
    internal static ReadOnlySpan<T> Without<T>(ReadOnlySpan<T> items, IReadOnlyList<int> dropped, ref T[] buffer)
    {
        if (dropped.Count == 0)
        {
            return items;
        }

        if (buffer.Length < items.Length)
        {
            buffer = new T[items.Length];
        }

        int kept = 0;
        int next = 0;
        for (int i = 0; i < items.Length; i++)
        {
            if (next < dropped.Count && dropped[next] == i)
            {
                next++;
                continue;
            }

            buffer[kept++] = items[i];
        }

        return buffer.AsSpan(0, kept);
    }

    /// <summary>
    /// <paramref name="items"/> without the ones at <paramref name="dropped"/>, as the other overload, and with each one
    /// at <paramref name="fading"/> (ascending indices) given to <paramref name="dim"/> with 1 minus its fade, so a Baked
    /// light whose bake is fading in gives way to it as it comes. With nothing fading it is the other overload.
    /// </summary>
    internal static ReadOnlySpan<T> Without<T>(
        ReadOnlySpan<T> items, IReadOnlyList<int> dropped, IReadOnlyList<(int Index, float Fade)> fading,
        Func<T, float, T> dim, ref T[] buffer)
    {
        if (fading.Count == 0)
        {
            return Without(items, dropped, ref buffer);
        }

        if (buffer.Length < items.Length)
        {
            buffer = new T[items.Length];
        }

        int kept = 0;
        int nextDropped = 0;
        int nextFading = 0;
        for (int i = 0; i < items.Length; i++)
        {
            if (nextDropped < dropped.Count && dropped[nextDropped] == i)
            {
                nextDropped++;
                continue;
            }

            var item = items[i];
            if (nextFading < fading.Count && fading[nextFading].Index == i)
            {
                item = dim(item, 1f - fading[nextFading].Fade);
                nextFading++;
            }

            buffer[kept++] = item;
        }

        return buffer.AsSpan(0, kept);
    }
}
