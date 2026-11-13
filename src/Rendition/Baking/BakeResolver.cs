using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>
/// Works out, once a frame, which baked areas have a fresh bake and so which lights the bake takes over, keeping every
/// array, list and file name it needs from one frame to the next so a steady frame allocates nothing. The renderer
/// keeps one. See "Each frame's bakes" in docs/design.md.
/// </summary>
/// <remarks>
/// The <see cref="BakeFrame"/> a call returns belongs to the resolver and is valid until the next call: the next call
/// writes the new frame into the same object. Keep what you need from it, not the frame. Not thread safe.
/// </remarks>
public sealed class BakeResolver
{
    private readonly BakeFrame _frame = new();
    private readonly BakeArrivals _arrivals = new();
    private readonly BakeFileNames _files = new();
    private readonly ModelOwners _models = new();
    private readonly BakeLightOwners _lights = new();
    private readonly SpaceDraws _spaceDraws = new();

    /// <summary>A resolver that asks <paramref name="store"/> whether each bake is fresh.</summary>
    public BakeResolver(IBakeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Store = store;
    }

    /// <summary>Where bakes are looked up. May be changed between frames.</summary>
    public IBakeStore Store
    {
        get;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>
    /// Asked, for each chunk whose bake the store holds fresh, whether that bake is on the GPU; a chunk only counts as
    /// <see cref="BakeFrame.ChunkFresh"/> when it is, and as <see cref="BakeFrame.ChunkPending"/> otherwise. The
    /// renderer sets it to its chunk bake pool, which starts the load the first time a file is asked about. With none,
    /// a fresh bake counts as there.
    /// </summary>
    internal Func<string, BakeInputs, bool>? ChunkResident { get; set; }

    /// <summary>
    /// How long, in seconds of <see cref="FrameView.Time"/>, a bake that arrives while its area is drawn live takes to
    /// fade in: <see cref="BakeFrame.SpaceFade"/> and <see cref="BakeFrame.ChunkFade"/> rise over it. 0 shows every bake
    /// at once. A resolver made on its own starts at 0, so <see cref="BakeFreshness.Resolve"/> never fades; the
    /// renderer's starts at 0.5, the default of <see cref="Renderer.BakeFadeSeconds"/>, which checks the range and sets
    /// this.
    /// </summary>
    internal float FadeSeconds
    {
        get => _arrivals.FadeSeconds;
        set => _arrivals.FadeSeconds = value;
    }

    /// <summary>
    /// Forgets which areas were drawn live, as a frame that names no area does, so the next frame that names one is its
    /// first and a bake fresh then shows at once. The renderer calls it for a frame it doesn't resolve.
    /// </summary>
    internal void ForgetArrivals() => _arrivals.Clear();

    /// <summary>
    /// Hashes each of the frame's baked areas, asks <see cref="Store"/> whether its bake is fresh, and decides how each
    /// light is used. Every Mixed or Baked light is first given to <paramref name="animated"/> with its world-space
    /// hash, and one that is moving is left out of its area's inputs and stays real-time. Realtime lights are never
    /// hashed or tracked. The space's draws are every static draw in the frame, since all of them block its light,
    /// plus where each chunk that owns some stands (<see cref="BakeInputHash.PlacedChunk"/>); a chunk's are only the
    /// models it owns (<see cref="BakeAreas.OwnerOfModel"/>), with every part of a model filed together. The frame's
    /// chunks must already pass <see cref="BakeFiles.IsRigid"/>, as <see cref="Renderer.Render"/> checks. Each area is
    /// hashed at its own <see cref="BakedSpace.TexelsPerMetre"/> or <see cref="BakedChunk.TexelsPerMetre"/>.
    /// </summary>
    /// <returns>
    /// The frame, which the resolver owns and writes again on the next call. A frame that names no space and no chunks
    /// returns one shared empty frame at once, without hashing anything, asking the store or updating
    /// <paramref name="animated"/>.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException">An area's texel density is outside 1 to 200.</exception>
    public BakeFrame Resolve(
        in FrameView frame, ReadOnlySpan<DrawItem> draws, Func<DetailHandle, BakeSphere> bounds, ulong environment,
        AnimatedLights animated)
    {
        var space = frame.BakedSpace;
        var chunks = frame.BakedChunks;
        if (space is null && chunks.IsEmpty)
        {
            _models.Clear();
            _lights.Clear();
            _arrivals.Clear();
            return BakeFrame.None;
        }

        _arrivals.BeginFrame();
        _models.Reset(draws.Length, chunks.Length);
        _frame.Reset();
        _lights.Track(frame.Lights, frame.LocalLights, animated, frame.Time);
        _lights.Assign(frame.LocalLights, space, chunks, animated);

        int staticCount = SpaceDraws.StaticCount(draws);
        if (!chunks.IsEmpty && staticCount > 0)
        {
            _models.File(draws, staticCount, bounds, space, chunks);
        }

        ResolveChunks(chunks, animated, frame.Time);
        if (space is { } baked)
        {
            ResolveSpace(frame, baked, _spaceDraws.Ids(draws, staticCount), animated, environment);
        }

        _lights.HandOver(_frame, frame.Lights, frame.LocalLights, animated);
        _arrivals.ForgetUnnamed();
        return _frame;
    }

    /// <summary>
    /// The bake ids of the static draws chunk <paramref name="chunk"/> owned in the last <see cref="Resolve"/>, filed
    /// as its inputs were, so a chunk's bake takes exactly the draws its inputs name.
    /// </summary>
    internal IReadOnlyList<ulong> ChunkIds(int chunk) => _models.ChunkIds(chunk);

    /// <summary>
    /// For each draw of the last <see cref="Resolve"/>, the index of the chunk that owns its model, or
    /// <see cref="BakeAreas.None"/> for a draw no chunk owns: a moving one, or one in the space or outside every area.
    /// Empty after a frame that names no area.
    /// </summary>
    public ReadOnlySpan<int> Owners => _models.Owners;

    /// <summary>
    /// For each local light of the last <see cref="Resolve"/>, the area that owns it: <see cref="BakeAreas.Space"/>,
    /// a chunk's index, or <see cref="BakeAreas.None"/> for a Realtime or moving light or one outside every area.
    /// </summary>
    internal ReadOnlySpan<int> LightOwners => _lights.Owners;

    /// <summary>
    /// For each local light of the last <see cref="Resolve"/> that a chunk owns, its <see cref="BakeInputHash.Light"/>
    /// in that chunk's space, as the chunk's bake lists its mask channels; 0 for any other light.
    /// </summary>
    internal ReadOnlySpan<ulong> LightChunkHashes => _lights.ChunkHashes;

    /// <summary>The bake file of the space named <paramref name="name"/>, made once and kept.</summary>
    internal string SpaceFile(string name) => _files.Space(name);

    // Hashes each chunk from the draws and lights it owns, and asks whether its bake is fresh and on the GPU.
    private void ResolveChunks(ReadOnlySpan<BakedChunk> chunks, AnimatedLights animated, float time)
    {
        for (int c = 0; c < chunks.Length; c++)
        {
            var ids = _models.ChunkIds(c);
            var inputs = BakeInputHash.Chunk(
                CollectionsMarshal.AsSpan(ids), _lights.ChunkLights(c), chunks[c],
                new BakeSettings(chunks[c].TexelsPerMetre));
            string file = _files.Chunk(chunks[c].Name, inputs.Combined);
            // A bake fresh in the store only counts once it is on the GPU; until then the chunk is pending.
            bool stored = IsFresh(file, inputs, animated);
            bool resident = stored && (ChunkResident is not { } isResident || isResident(file, inputs));
            bool pending = stored && !resident;
            _frame.AddChunk(inputs, resident, pending, file, ids.Count, _arrivals.Arrive(file, resident, pending, time));
        }
    }

    // Hashes the space from every static draw, the chunks that own some, and the lights it owns, and asks whether its
    // bake is fresh.
    private void ResolveSpace(
        in FrameView frame, BakedSpace space, ReadOnlySpan<ulong> ids, AnimatedLights animated, ulong environment)
    {
        var inputs = BakeInputHash.Space(
            ids, _lights.SpaceLights(), space, environment, _lights.SpaceSuns(frame.Lights, animated),
            new BakeSettings(space.TexelsPerMetre), _spaceDraws.PlacedChunks(frame.BakedChunks, _frame, _models));
        string file = _files.Space(space.Name);
        bool fresh = IsFresh(file, inputs, animated);
        _frame.SetSpace(inputs, fresh, _arrivals.Arrive(file, fresh, false, frame.Time));
    }

    // Until the tracker has settled, a light that moves may not have been seen moving yet, so a bake can look stale for
    // its first frames only because that light is still in its inputs. It is asked quietly then, and only a bake still
    // stale once the tracker has settled is reported.
    private bool IsFresh(string file, BakeInputs inputs, AnimatedLights animated) =>
        animated.Settled ? Store.IsFresh(file, inputs) : Store.IsFreshQuietly(file, inputs);


}
