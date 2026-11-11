using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>Where saved bakes are looked up. See "Baked lighting" in docs/rendering.md.</summary>
public interface IBakeStore
{
    /// <summary>
    /// Whether the bake at <paramref name="file"/>, such as <c>lighting/courtyard.lightbake</c>, exists and was made
    /// from exactly <paramref name="inputs"/>.
    /// </summary>
    bool IsFresh(string file, BakeInputs inputs);
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
/// the Baked lights a fresh bake takes over, which the frame then leaves out. Made by <see cref="BakeFreshness.Resolve"/>.
/// </summary>
public sealed class BakeFrame
{
    /// <summary>A frame that names no baked area: nothing is hashed, nothing is fresh, and every light is real-time.</summary>
    internal static readonly BakeFrame None = new(null, [], false, [], new Dictionary<LightKey, LightMode>(), [], []);

    private readonly Dictionary<LightKey, LightMode> _modes;

    internal BakeFrame(
        BakeInputs? space, BakeInputs[] chunks, bool spaceFresh, bool[] chunkFresh, Dictionary<LightKey, LightMode> modes,
        int[] droppedLocalLights, int[] droppedSuns)
    {
        Space = space;
        Chunks = chunks;
        SpaceFresh = spaceFresh;
        ChunkFresh = chunkFresh;
        _modes = modes;
        DroppedLocalLights = droppedLocalLights;
        DroppedSuns = droppedSuns;
    }

    /// <summary>The space's inputs, or null when the frame names no space.</summary>
    public BakeInputs? Space { get; }

    /// <summary>Each chunk's inputs, in the frame's order.</summary>
    public IReadOnlyList<BakeInputs> Chunks { get; }

    /// <summary>Whether the store holds a bake of the space made from <see cref="Space"/>.</summary>
    public bool SpaceFresh { get; }

    /// <summary>For each chunk, whether the store holds a bake made from its inputs.</summary>
    public IReadOnlyList<bool> ChunkFresh { get; }

    /// <summary>
    /// How the light is used this frame: its own mode when the area that owns it has a fresh bake and it isn't
    /// moving, and <see cref="LightMode.Realtime"/> otherwise, as for a light this frame doesn't have.
    /// </summary>
    public LightMode ModeOf(LightKey key) => _modes.TryGetValue(key, out var mode) ? mode : LightMode.Realtime;

    /// <summary>The indices, ascending, of the frame's point and spot lights that are Baked and over a fresh bake.</summary>
    public IReadOnlyList<int> DroppedLocalLights { get; }

    /// <summary>The indices, ascending, of the frame's suns that are Baked and over a fresh space bake.</summary>
    public IReadOnlyList<int> DroppedSuns { get; }
}

/// <summary>
/// Works out, once a frame, which baked areas have a fresh bake and so which lights the bake takes over. See
/// "Is the bake fresh?" in docs/design.md.
/// </summary>
public static class BakeFreshness
{
    /// <summary>
    /// Hashes each of the frame's baked areas, asks <paramref name="store"/> whether its bake is fresh, and decides how
    /// each light is used. Every Mixed or Baked light is first given to <paramref name="animated"/> with its world-space
    /// hash, and one that is moving is left out of its area's inputs and stays real-time. Realtime lights are never
    /// hashed or tracked. The space's draws are every static draw in the frame, since all of them block its light;
    /// a chunk's are only the ones it owns (<see cref="BakeAreas.OwnerOfDraw"/>). The frame's chunks must already
    /// pass <see cref="BakeFiles.IsRigid"/>, as <see cref="Renderer.Render"/> checks.
    /// </summary>
    /// <remarks>
    /// A frame that names no space and no chunks returns one shared empty frame at once, without hashing anything,
    /// asking the store or updating <paramref name="animated"/>. Otherwise it allocates the frame it returns.
    /// </remarks>
    public static BakeFrame Resolve(
        in FrameView frame, ReadOnlySpan<DrawItem> draws, Func<DetailHandle, BakeSphere> bounds, ulong environment,
        BakeSettings settings, AnimatedLights animated, IBakeStore store)
    {
        var space = frame.BakedSpace;
        var chunks = frame.BakedChunks;
        if (space is null && chunks.IsEmpty)
        {
            return BakeFrame.None;
        }

        var suns = frame.Lights;
        var locals = frame.LocalLights;

        // Every Mixed or Baked light's world-space hash goes to the tracker first, so a moving one is known before
        // any area is hashed.
        var sunHashes = new ulong[suns.Length];
        var localHashes = new ulong[locals.Length];
        var tracked = new (LightKey Key, ulong Hash, bool Spot)[suns.Length + locals.Length];
        int trackedCount = 0;
        for (int i = 0; i < suns.Length; i++)
        {
            if (suns[i].Mode != LightMode.Realtime)
            {
                sunHashes[i] = BakeInputHash.Sun(suns[i]);
                tracked[trackedCount++] = (new LightKey(true, i), sunHashes[i], false);
            }
        }

        for (int i = 0; i < locals.Length; i++)
        {
            if (locals[i].Mode != LightMode.Realtime)
            {
                localHashes[i] = BakeInputHash.Light(locals[i], Matrix4x4.Identity);
                tracked[trackedCount++] = (new LightKey(false, locals[i].Key), localHashes[i], locals[i].IsSpot);
            }
        }

        animated.Update(tracked.AsSpan(0, trackedCount), frame.Time);

        // Each chunk's draws and lights, gathered in one pass over each. A chunk's lights are hashed in its own space,
        // so every placed copy gives the same hash.
        var chunkIds = new List<ulong>?[chunks.Length];
        var chunkLights = new List<ulong>?[chunks.Length];
        var toChunks = new Matrix4x4[chunks.Length];
        for (int c = 0; c < chunks.Length; c++)
        {
            Matrix4x4.Invert(chunks[c].Transform, out toChunks[c]);
        }

        // Which area owns each light that can join a bake: BakeAreas.None for a Realtime or moving one.
        var lightOwners = new int[locals.Length];
        for (int i = 0; i < locals.Length; i++)
        {
            lightOwners[i] = InBake(locals[i].Mode, new LightKey(false, locals[i].Key), animated)
                ? BakeAreas.OwnerOfLight(locals[i].Position, space, chunks)
                : BakeAreas.None;
            if (lightOwners[i] >= 0)
            {
                (chunkLights[lightOwners[i]] ??= []).Add(BakeInputHash.Light(locals[i], toChunks[lightOwners[i]]));
            }
        }

        int staticCount = 0;
        for (int i = 0; i < draws.Length; i++)
        {
            if (draws[i].BakeId == 0)
            {
                continue;
            }

            staticCount++;
            if (chunks.IsEmpty)
            {
                continue;
            }

            var world = BakeAreas.Transform(bounds(draws[i].Detail), draws[i].Transform);
            int owner = BakeAreas.OwnerOfDraw(world, space, chunks);
            if (owner >= 0)
            {
                (chunkIds[owner] ??= []).Add(draws[i].BakeId);
            }
        }

        BakeInputs? spaceInputs = null;
        bool spaceFresh = false;
        if (space is { } baked)
        {
            spaceInputs = SpaceInputs(baked, draws, staticCount, locals, localHashes, lightOwners, suns, sunHashes,
                animated, environment, settings);
            spaceFresh = store.IsFresh(BakeFiles.Space(baked.Name), spaceInputs.Value);
        }

        var chunkInputs = new BakeInputs[chunks.Length];
        var chunkFresh = new bool[chunks.Length];
        for (int c = 0; c < chunks.Length; c++)
        {
            chunkInputs[c] = BakeInputHash.Chunk(
                chunkIds[c] is { } ids ? CollectionsMarshal.AsSpan(ids) : [],
                chunkLights[c] is { } lights ? CollectionsMarshal.AsSpan(lights) : [],
                chunks[c], settings);
            chunkFresh[c] = store.IsFresh(BakeFiles.Chunk(chunks[c].Name, chunkInputs[c].Combined), chunkInputs[c]);
        }

        // A light over a fresh bake keeps its mode, and a Baked one is left to the bake.
        var modes = new Dictionary<LightKey, LightMode>();
        var droppedSuns = new List<int>();
        var droppedLocals = new List<int>();
        for (int i = 0; i < suns.Length; i++)
        {
            var key = new LightKey(true, i);
            if (spaceFresh && InBake(suns[i].Mode, key, animated))
            {
                modes[key] = suns[i].Mode;
                if (suns[i].Mode == LightMode.Baked)
                {
                    droppedSuns.Add(i);
                }
            }
        }

        for (int i = 0; i < locals.Length; i++)
        {
            int owner = lightOwners[i];
            bool fresh = owner == BakeAreas.Space ? spaceFresh : owner >= 0 && chunkFresh[owner];
            if (fresh)
            {
                modes[new LightKey(false, locals[i].Key)] = locals[i].Mode;
                if (locals[i].Mode == LightMode.Baked)
                {
                    droppedLocals.Add(i);
                }
            }
        }

        return new BakeFrame(spaceInputs, chunkInputs, spaceFresh, chunkFresh, modes, [.. droppedLocals], [.. droppedSuns]);
    }

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

    private static bool InBake(LightMode mode, LightKey key, AnimatedLights animated) =>
        mode != LightMode.Realtime && !animated.IsAnimated(key);

    private static BakeInputs SpaceInputs(
        BakedSpace space, ReadOnlySpan<DrawItem> draws, int staticCount, ReadOnlySpan<LocalLight> locals,
        ulong[] localHashes, int[] lightOwners, ReadOnlySpan<DirectionalLight> suns, ulong[] sunHashes,
        AnimatedLights animated, ulong environment, BakeSettings settings)
    {
        // Every static draw in the frame blocks and bounces the space's light, wherever it stands.
        var ids = new ulong[staticCount];
        int idCount = 0;
        foreach (ref readonly var draw in draws)
        {
            if (draw.BakeId != 0)
            {
                ids[idCount++] = draw.BakeId;
            }
        }

        var lights = new List<ulong>();
        for (int i = 0; i < locals.Length; i++)
        {
            if (lightOwners[i] == BakeAreas.Space)
            {
                lights.Add(localHashes[i]);
            }
        }

        var sunsInBake = new List<ulong>();
        for (int i = 0; i < suns.Length; i++)
        {
            if (InBake(suns[i].Mode, new LightKey(true, i), animated))
            {
                sunsInBake.Add(sunHashes[i]);
            }
        }

        return BakeInputHash.Space(ids, [.. lights], space, environment, SunHash(sunsInBake), settings);
    }

    // One sun is hashed as itself; more than one, which is rare, as their count and hashes in ascending order.
    private static ulong? SunHash(List<ulong> suns)
    {
        if (suns.Count == 0)
        {
            return null;
        }

        if (suns.Count == 1)
        {
            return suns[0];
        }

        suns.Sort();
        var hasher = BakeInputHash.Hasher().Add(suns.Count);
        foreach (ulong sun in suns)
        {
            hasher.Add(sun);
        }

        return hasher.Finish();
    }
}
