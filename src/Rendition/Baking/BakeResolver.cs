using System.Numerics;
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
    // The file names kept at most, so a game whose chunks keep changing doesn't grow the cache for ever.
    private const int MaxCachedNames = 1024;

    private readonly BakeFrame _frame = new();
    private readonly ModelOrder _modelOrder = new();
    private readonly Comparison<int> _compareModels;
    private readonly List<BakeSphere> _modelParts = [];
    private readonly List<ulong> _spaceLights = [];
    private readonly List<ulong> _spaceSuns = [];
    private readonly List<ulong> _placedChunks = [];
    private readonly Dictionary<string, string> _spaceFiles = [];
    private readonly Dictionary<(string Name, ulong Hash), string> _chunkFiles = [];

    private ulong[] _sunHashes = [];
    private ulong[] _localHashes = [];
    private (LightKey Key, ulong Hash, bool Spot)[] _tracked = [];
    private int[] _lightOwners = [];
    private List<ulong>[] _chunkIds = [];
    private List<ulong>[] _chunkLights = [];
    private int[] _drawIndex = [];
    private (ulong Instance, Matrix4x4 Transform)[] _keys = [];
    private int[] _order = [];
    private ulong[] _spaceIds = [];

    /// <summary>A resolver that asks <paramref name="store"/> whether each bake is fresh.</summary>
    public BakeResolver(IBakeStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Store = store;
        _compareModels = _modelOrder.Compare;
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
            return BakeFrame.None;
        }

        var suns = frame.Lights;
        var locals = frame.LocalLights;
        var result = _frame;
        result.Reset();
        // Every Mixed or Baked light's world-space hash goes to the tracker first, so a moving one is known before
        // any area is hashed.
        Grow(ref _sunHashes, suns.Length);
        Grow(ref _localHashes, locals.Length);
        Grow(ref _tracked, suns.Length + locals.Length);
        int trackedCount = 0;
        for (int i = 0; i < suns.Length; i++)
        {
            if (suns[i].Mode != LightMode.Realtime)
            {
                _sunHashes[i] = BakeInputHash.Sun(suns[i]);
                _tracked[trackedCount++] = (new LightKey(true, i), _sunHashes[i], false);
            }
        }

        for (int i = 0; i < locals.Length; i++)
        {
            if (locals[i].Mode != LightMode.Realtime)
            {
                _localHashes[i] = BakeInputHash.Light(locals[i], Matrix4x4.Identity);
                _tracked[trackedCount++] = (new LightKey(false, locals[i].Key), _localHashes[i], locals[i].IsSpot);
            }
        }

        animated.Update(_tracked.AsSpan(0, trackedCount), frame.Time);

        // Each chunk's draws and lights, gathered in one pass over each. A chunk's lights are hashed in its own space,
        // so every placed copy gives the same hash.
        GrowLists(ref _chunkIds, chunks.Length);
        GrowLists(ref _chunkLights, chunks.Length);
        for (int c = 0; c < chunks.Length; c++)
        {
            _chunkIds[c].Clear();
            _chunkLights[c].Clear();
        }

        // Which area owns each light that can join a bake: BakeAreas.None for a Realtime or moving one.
        Grow(ref _lightOwners, locals.Length);
        for (int i = 0; i < locals.Length; i++)
        {
            _lightOwners[i] = InBake(locals[i].Mode, new LightKey(false, locals[i].Key), animated)
                ? BakeAreas.OwnerOfLight(locals[i].Position, space, chunks)
                : BakeAreas.None;
            if (_lightOwners[i] >= 0)
            {
                _chunkLights[_lightOwners[i]].Add(BakeInputHash.Light(locals[i], chunks[_lightOwners[i]].Transform));
            }
        }

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
            FileModels(draws, staticCount, bounds, space, chunks);
        }

        for (int c = 0; c < chunks.Length; c++)
        {
            var inputs = BakeInputHash.Chunk(
                CollectionsMarshal.AsSpan(_chunkIds[c]), CollectionsMarshal.AsSpan(_chunkLights[c]), chunks[c],
                new BakeSettings(chunks[c].TexelsPerMetre));
            result.AddChunk(inputs, IsFresh(ChunkFile(chunks[c].Name, inputs.Combined), inputs, animated));
        }

        if (space is { } baked)
        {
            // A chunk's draws have ids relative to it, so where each chunk that owns some stands goes in too.
            _placedChunks.Clear();
            for (int c = 0; c < chunks.Length; c++)
            {
                if (_chunkIds[c].Count > 0)
                {
                    _placedChunks.Add(BakeInputHash.PlacedChunk(chunks[c], result.ChunkInputs[c].Draws));
                }
            }

            var spaceInputs = SpaceInputs(baked, draws, staticCount, locals, suns, animated, environment);
            result.SetSpace(spaceInputs, IsFresh(SpaceFile(baked.Name), spaceInputs, animated));
        }

        // A light over a fresh bake keeps its mode, and a Baked one is left to the bake.
        for (int i = 0; i < suns.Length; i++)
        {
            var key = new LightKey(true, i);
            if (result.SpaceFresh && InBake(suns[i].Mode, key, animated))
            {
                result.SetMode(key, suns[i].Mode);
                if (suns[i].Mode == LightMode.Baked)
                {
                    result.DropSun(i);
                }
            }
        }

        for (int i = 0; i < locals.Length; i++)
        {
            int owner = _lightOwners[i];
            bool fresh = owner == BakeAreas.Space ? result.SpaceFresh : owner >= 0 && result.ChunkFresh[owner];
            if (fresh)
            {
                result.SetMode(new LightKey(false, locals[i].Key), locals[i].Mode);
                if (locals[i].Mode == LightMode.Baked)
                {
                    result.DropLocalLight(i);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The bake ids of the static draws chunk <paramref name="chunk"/> owned in the last <see cref="Resolve"/>, filed
    /// as its inputs were, so a chunk's bake takes exactly the draws its inputs name.
    /// </summary>
    internal IReadOnlyList<ulong> ChunkIds(int chunk) => _chunkIds[chunk];

    // A model's parts are filed together, under the area that owns the whole model, the same call Super made when it
    // gave their ids, so a model straddling two areas is never split between them. The parts need not come one after
    // another: they are sorted by (instance, transform, draw order) first.
    private void FileModels(
        ReadOnlySpan<DrawItem> draws, int staticCount, Func<DetailHandle, BakeSphere> bounds, BakedSpace? space,
        ReadOnlySpan<BakedChunk> chunks)
    {
        Grow(ref _drawIndex, staticCount);
        Grow(ref _keys, staticCount);
        Grow(ref _order, staticCount);
        for (int i = 0, n = 0; i < draws.Length; i++)
        {
            if (draws[i].BakeId != 0)
            {
                _drawIndex[n] = i;
                _keys[n] = (BakeIds.Instance(draws[i].BakeId), draws[i].Transform);
                _order[n] = n;
                n++;
            }
        }

        // Sorting with an IComparer makes a Comparison delegate on every call, so the delegate is made once instead.
        _modelOrder.Keys = _keys;
        _order.AsSpan(0, staticCount).Sort(_compareModels);

        for (int first = 0; first < staticCount;)
        {
            int end = first + 1;
            while (end < staticCount && _keys[_order[end]] == _keys[_order[first]])
            {
                end++;
            }

            _modelParts.Clear();
            for (int p = first; p < end; p++)
            {
                ref readonly var part = ref draws[_drawIndex[_order[p]]];
                _modelParts.Add(BakeAreas.Transform(bounds(part.Detail), part.Transform));
            }

            int owner = BakeAreas.OwnerOfModel(CollectionsMarshal.AsSpan(_modelParts), space, chunks);
            if (owner >= 0)
            {
                var ids = _chunkIds[owner];
                for (int p = first; p < end; p++)
                {
                    ids.Add(draws[_drawIndex[_order[p]]].BakeId);
                }
            }

            first = end;
        }
    }

    private BakeInputs SpaceInputs(
        BakedSpace space, ReadOnlySpan<DrawItem> draws, int staticCount, ReadOnlySpan<LocalLight> locals,
        ReadOnlySpan<DirectionalLight> suns, AnimatedLights animated, ulong environment)
    {
        // Every static draw in the frame blocks and bounces the space's light, wherever it stands.
        Grow(ref _spaceIds, staticCount);
        int idCount = 0;
        foreach (ref readonly var draw in draws)
        {
            if (draw.BakeId != 0)
            {
                _spaceIds[idCount++] = draw.BakeId;
            }
        }

        _spaceLights.Clear();
        for (int i = 0; i < locals.Length; i++)
        {
            if (_lightOwners[i] == BakeAreas.Space)
            {
                _spaceLights.Add(_localHashes[i]);
            }
        }

        _spaceSuns.Clear();
        for (int i = 0; i < suns.Length; i++)
        {
            if (InBake(suns[i].Mode, new LightKey(true, i), animated))
            {
                _spaceSuns.Add(_sunHashes[i]);
            }
        }

        return BakeInputHash.Space(
            _spaceIds.AsSpan(0, idCount), CollectionsMarshal.AsSpan(_spaceLights), space, environment, SunHash(),
            new BakeSettings(space.TexelsPerMetre), CollectionsMarshal.AsSpan(_placedChunks));
    }

    // One sun is hashed as itself; more than one, which is rare, as their count and hashes in ascending order.
    private ulong? SunHash()
    {
        if (_spaceSuns.Count == 0)
        {
            return null;
        }

        if (_spaceSuns.Count == 1)
        {
            return _spaceSuns[0];
        }

        _spaceSuns.Sort();
        var hasher = BakeInputHash.Hasher().Add(_spaceSuns.Count);
        foreach (ulong sun in _spaceSuns)
        {
            hasher.Add(sun);
        }

        return hasher.Finish();
    }

    // The file names are worked out once for each distinct key, since building one allocates a string.
    internal string SpaceFile(string name)
    {
        if (!_spaceFiles.TryGetValue(name, out var file))
        {
            if (_spaceFiles.Count >= MaxCachedNames)
            {
                _spaceFiles.Clear();
            }

            file = _spaceFiles[name] = BakeFiles.Space(name);
        }

        return file;
    }

    private string ChunkFile(string name, ulong hash)
    {
        if (!_chunkFiles.TryGetValue((name, hash), out var file))
        {
            if (_chunkFiles.Count >= MaxCachedNames)
            {
                _chunkFiles.Clear();
            }

            file = _chunkFiles[(name, hash)] = BakeFiles.Chunk(name, hash);
        }

        return file;
    }

    // Until the tracker has settled, a light that moves may not have been seen moving yet, so a bake can look stale for
    // its first frames only because that light is still in its inputs. It is asked quietly then, and only a bake still
    // stale once the tracker has settled is reported.
    private bool IsFresh(string file, BakeInputs inputs, AnimatedLights animated) =>
        animated.Settled ? Store.IsFresh(file, inputs) : Store.IsFreshQuietly(file, inputs);

    private static bool InBake(LightMode mode, LightKey key, AnimatedLights animated) =>
        mode != LightMode.Realtime && !animated.IsAnimated(key);

    // Arrays only grow, so a frame with fewer lights or draws than an earlier one reuses what is there.
    private static void Grow<T>(ref T[] array, int length)
    {
        if (array.Length < length)
        {
            array = new T[Math.Max(length, array.Length * 2)];
        }
    }

    private static void GrowLists(ref List<ulong>[] lists, int length)
    {
        if (lists.Length >= length)
        {
            return;
        }

        int old = lists.Length;
        Array.Resize(ref lists, Math.Max(length, old * 2));
        for (int i = old; i < lists.Length; i++)
        {
            lists[i] = [];
        }
    }

    // Orders static draws, by their position among the static draws, by (instance, the 16 transform elements, position).
    // Every part of a model has the model's instance (BakeIds.Instance) and transform, so a model's parts end up in a
    // run in draw order wherever other models' draws came between them. Two copies of a chunk can give a model the same
    // instance, but they stand in different places, so the transform keeps them apart. One instance is kept and given
    // the frame's keys each time, so ordering makes no garbage.
    private sealed class ModelOrder
    {
        public (ulong Instance, Matrix4x4 Transform)[] Keys { get; set; } = [];

        public int Compare(int a, int b)
        {
            var (instanceA, x) = Keys[a];
            var (instanceB, y) = Keys[b];
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
}
