using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// Works out each frame's baked areas and what the frame draws of them: the space's lightmaps and probes, the relit
/// chunks and the frame's mask slots, faded in as far as they have come, and the frame's lights without those a fresh
/// bake takes over. See "Each frame's bakes" in docs/design.md.
/// </summary>
internal sealed class FrameBakes(
    ReadableBakes readable, ChunkBakePool pool, RetiredResources retired, AreaInputs inputs, SpaceBake space)
{
    private readonly ChunkResidency _residency = new(pool, readable);
    private readonly BakedLightFilter _filter = new();

    // Whether this frame lights the baked space with its bake's probes, and a hash of the chunk bakes it draws.
    private bool _spaceProbesActive;
    private ulong _chunksHash;

    /// <summary>Works out which areas are fresh, and when each bake arrived, for the fade.</summary>
    public BakeResolver Resolver { get; } = new(NoBakes.Instance) { FadeSeconds = 0.5f };

    /// <summary>The placed chunks this frame relights.</summary>
    public ChunkRelight Relight { get; } = new(pool, readable);

    /// <summary>While this frame draws a bake, which of its Mixed lights reads each of the frame's four mask slots.</summary>
    public FrameMaskSlots MaskSlots { get; } = new();

    /// <summary>What the last frame's baked areas came to, or null when it named no baked space or chunk.</summary>
    public BakeFrame? Last { get; private set; }

    /// <summary>
    /// The space's lightmap rectangles this frame draws, by bake id: the loaded space bake's while it is drawn, or null.
    /// A chunk's rectangles are kept apart, in <see cref="ChunkRelight.Lightmaps"/>, because copies of one chunk name
    /// can have different bakes with the same ids.
    /// </summary>
    public IReadOnlyDictionary<ulong, GpuLightmapRect>? SpaceLightmaps { get; private set; }

    /// <summary>The loaded bake's probes while this frame lights the baked space with them, or null when it doesn't.</summary>
    public SpaceBake.BakedProbes? SpaceProbes => _spaceProbesActive ? space.Probes : null;

    /// <summary>How far the frame's space bake has faded in, for the frame uniforms: 1 unless one is fading in.</summary>
    public float SpaceFade { get; private set; } = 1f;

    /// <summary>Whether a bake this frame draws is still fading in: the space's, or a relit chunk's.</summary>
    public bool Fading { get; private set; }

    /// <summary>
    /// A number that changes when the bakes the frame draws do: the space bake's inputs while it is drawn, and the
    /// chunk bakes the frame names. Reflection captures light surfaces with them.
    /// </summary>
    public ulong DrawnKey => (SpaceLightmaps is not null || SpaceProbes is not null ? space.Inputs : 0) ^ _chunksHash;

    /// <summary>
    /// Works out the frame's baked areas, and is true when a fresh bake takes some lights over: then
    /// <paramref name="lit"/> is the frame without them. A frame with no baked area is left alone, unhashed.
    /// </summary>
    public bool TryResolve(in FrameView view, ReadOnlySpan<DrawItem> draws, IBakeStore store, out FrameView lit)
    {
        lit = default;
        MaskSlots.Reset();
        if (view.BakedSpace is null && view.BakedChunks.IsEmpty)
        {
            Forget();
            return false;
        }

        FollowStore(store);
        var bakes = ResolveFrame(view, draws);
        Use(view, draws.Length, bakes);
        return _filter.TryWithout(view, bakes, out lit);
    }

    /// <summary>For a frame that names no baked area: draws no bake, and forgets when the last bakes arrived.</summary>
    private void Forget()
    {
        Last = null;
        SpaceLightmaps = null;
        _chunksHash = 0;
        _spaceProbesActive = false;
        Relight.Forget();
        SpaceFade = 1f;
        Fading = false;
        // The next frame that names an area is its first, so a bake fresh then shows at once.
        Resolver.ForgetArrivals();
    }

    /// <summary>
    /// Follows the store: a bake that couldn't be read is tried again only once the store is another or its files may
    /// have changed. The chunk bakes on the GPU go too, so each is read again from the store as it is now.
    /// </summary>
    private void FollowStore(IBakeStore store)
    {
        if (!ReferenceEquals(readable.Inner, store) || readable.ReloadsSeen != store.Reloads)
        {
            readable.Inner = store;
            readable.ReloadsSeen = store.Reloads;
            readable.Unreadable.Clear();
            var destroying = retired.AfterLastSubmitted;
            pool.Clear(destroying.FreedTextures, destroying.FreedOthers);
        }

        // Files the store forgot one at a time are read again, and only those: the other chunk bakes stay up.
        foreach (string file in store.TakeForgotten())
        {
            readable.Unreadable.Remove(file);
            var destroying = retired.AfterLastSubmitted;
            pool.Forget(file, destroying.FreedTextures, destroying.FreedOthers);
        }
    }

    /// <summary>
    /// Works out which of the frame's baked areas are fresh, and loads the space's bake. With baked lighting off, every
    /// bake counts as missing, so the frame is drawn as if nothing were baked.
    /// </summary>
    private BakeFrame ResolveFrame(in FrameView view, ReadOnlySpan<DrawItem> draws)
    {
        Resolver.Store = view.Off.HasFlag(RenderEffects.BakedLighting) ? NoBakes.Instance : readable;
        Resolver.ChunkResident = _residency.Question;
        _residency.ClearNamed();
        var bakes = inputs.ResolveAreas(Resolver, view, draws);
        if (bakes.SpaceFresh && !space.Load(Resolver.SpaceFile(view.BakedSpace!.Value.Name), bakes.Space!.Value))
        {
            // Its file is now unreadable, so the space is worked out again as unbaked: its Baked lights come back.
            _residency.ClearNamed();
            bakes = inputs.ResolveAreas(Resolver, view, draws);
        }

        return bakes;
    }

    /// <summary>
    /// Draws the fresh bakes this frame: the space's lightmaps and probes, the relit chunks and the mask slots, faded
    /// in as far as they have come.
    /// </summary>
    private void Use(in FrameView view, int drawCount, BakeFrame bakes)
    {
        var spaceRects = bakes.SpaceFresh && space.Rects is { Count: > 0 } rects ? rects : null;
        SpaceLightmaps = spaceRects;
        _chunksHash = _residency.NamedHash();
        Relight.Find(view, drawCount, bakes, Resolver);
        _spaceProbesActive = bakes.SpaceFresh;
        if (spaceRects is not null || Relight.Active)
        {
            Relight.AssignMaskSlots(MaskSlots, view, bakes, spaceRects is not null ? space.Channels : null, Resolver);
        }

        Last = bakes;
        SpaceFade = spaceRects is not null || SpaceProbes is not null ? bakes.SpaceFade : 1f;
        Fading = SpaceFade < 1f || Relight.Fading;
    }
}
