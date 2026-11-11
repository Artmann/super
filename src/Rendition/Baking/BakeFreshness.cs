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
    /// hashed or tracked. The space's draws are every static draw in the frame, since all of them block its light,
    /// plus where each chunk that owns some stands (<see cref="BakeInputHash.PlacedChunk"/>); a chunk's are only the
    /// models it owns (<see cref="BakeAreas.OwnerOfModel"/>), with every part of a model filed together. The frame's
    /// chunks must already pass <see cref="BakeFiles.IsRigid"/>, as <see cref="Renderer.Render"/> checks.
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

        // Which area owns each light that can join a bake: BakeAreas.None for a Realtime or moving one.
        var lightOwners = new int[locals.Length];
        for (int i = 0; i < locals.Length; i++)
        {
            lightOwners[i] = InBake(locals[i].Mode, new LightKey(false, locals[i].Key), animated)
                ? BakeAreas.OwnerOfLight(locals[i].Position, space, chunks)
                : BakeAreas.None;
            if (lightOwners[i] >= 0)
            {
                (chunkLights[lightOwners[i]] ??= []).Add(BakeInputHash.Light(locals[i], chunks[lightOwners[i]].Transform));
            }
        }

        // A model's parts are filed together, under the area that owns the whole model, the same call Super made when
        // it gave their ids, so a model straddling two areas is never split between them.
        // The parts need not come one after another: they are sorted by (instance, transform, draw order) first.
        int staticCount = 0;
        for (int i = 0; i < draws.Length; i++)
        {
            if (draws[i].BakeId != 0)
            {
                staticCount++;
            }
        }

        if (!chunks.IsEmpty && staticCount > 0)
        {
            var drawIndex = new int[staticCount];
            var keys = new (ulong Instance, Matrix4x4 Transform)[staticCount];
            var order = new int[staticCount];
            for (int i = 0, n = 0; i < draws.Length; i++)
            {
                if (draws[i].BakeId != 0)
                {
                    drawIndex[n] = i;
                    keys[n] = (BakeIds.Instance(draws[i].BakeId), draws[i].Transform);
                    order[n] = n;
                    n++;
                }
            }

            Array.Sort(order, new ModelOrder(keys));

            var modelParts = new List<BakeSphere>();
            for (int first = 0; first < staticCount;)
            {
                int end = first + 1;
                while (end < staticCount && keys[order[end]] == keys[order[first]])
                {
                    end++;
                }

                modelParts.Clear();
                for (int p = first; p < end; p++)
                {
                    ref readonly var part = ref draws[drawIndex[order[p]]];
                    modelParts.Add(BakeAreas.Transform(bounds(part.Detail), part.Transform));
                }

                int owner = BakeAreas.OwnerOfModel(CollectionsMarshal.AsSpan(modelParts), space, chunks);
                if (owner >= 0)
                {
                    var ids = chunkIds[owner] ??= [];
                    for (int p = first; p < end; p++)
                    {
                        ids.Add(draws[drawIndex[order[p]]].BakeId);
                    }
                }

                first = end;
            }
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

        BakeInputs? spaceInputs = null;
        bool spaceFresh = false;
        if (space is { } baked)
        {
            // A chunk's draws have ids relative to it, so where each chunk that owns some stands goes in too.
            var placed = new List<ulong>();
            for (int c = 0; c < chunks.Length; c++)
            {
                if (chunkIds[c] is not null)
                {
                    placed.Add(BakeInputHash.PlacedChunk(chunks[c], chunkInputs[c].Draws));
                }
            }

            spaceInputs = SpaceInputs(baked, draws, staticCount, CollectionsMarshal.AsSpan(placed), locals, localHashes,
                lightOwners, suns, sunHashes, animated, environment, settings);
            spaceFresh = store.IsFresh(BakeFiles.Space(baked.Name), spaceInputs.Value);
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

    // Orders static draws, by their position among the static draws, by (instance, the 16 transform elements, position).
    // Every part of a model has the model's instance (BakeIds.Instance) and transform, so a model's parts end up in a
    // run in draw order wherever other models' draws came between them. Two copies of a chunk can give a model the same
    // instance, but they stand in different places, so the transform keeps them apart.
    internal readonly struct ModelOrder((ulong Instance, Matrix4x4 Transform)[] keys) : IComparer<int>
    {
        public int Compare(int a, int b)
        {
            var (instanceA, x) = keys[a];
            var (instanceB, y) = keys[b];
            int result = instanceA.CompareTo(instanceB);
            if (result != 0)
            {
                return result;
            }

            result = x.M11.CompareTo(y.M11);
            result = result != 0 ? result : x.M12.CompareTo(y.M12);
            result = result != 0 ? result : x.M13.CompareTo(y.M13);
            result = result != 0 ? result : x.M14.CompareTo(y.M14);
            result = result != 0 ? result : x.M21.CompareTo(y.M21);
            result = result != 0 ? result : x.M22.CompareTo(y.M22);
            result = result != 0 ? result : x.M23.CompareTo(y.M23);
            result = result != 0 ? result : x.M24.CompareTo(y.M24);
            result = result != 0 ? result : x.M31.CompareTo(y.M31);
            result = result != 0 ? result : x.M32.CompareTo(y.M32);
            result = result != 0 ? result : x.M33.CompareTo(y.M33);
            result = result != 0 ? result : x.M34.CompareTo(y.M34);
            result = result != 0 ? result : x.M41.CompareTo(y.M41);
            result = result != 0 ? result : x.M42.CompareTo(y.M42);
            result = result != 0 ? result : x.M43.CompareTo(y.M43);
            result = result != 0 ? result : x.M44.CompareTo(y.M44);
            return result != 0 ? result : a.CompareTo(b);
        }
    }

    private static BakeInputs SpaceInputs(
        BakedSpace space, ReadOnlySpan<DrawItem> draws, int staticCount, ReadOnlySpan<ulong> placedChunks,
        ReadOnlySpan<LocalLight> locals, ulong[] localHashes, int[] lightOwners, ReadOnlySpan<DirectionalLight> suns,
        ulong[] sunHashes, AnimatedLights animated, ulong environment, BakeSettings settings)
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

        return BakeInputHash.Space(ids, [.. lights], space, environment, SunHash(sunsInBake), settings, placedChunks);
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
