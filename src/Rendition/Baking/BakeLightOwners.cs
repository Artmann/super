using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>
/// Hashes a frame's Mixed and Baked lights, tells the animation tracker about them, and works out which area's bake
/// each one joins, keeping its arrays from frame to frame so a steady frame allocates nothing. Part of
/// <see cref="BakeResolver"/>.
/// </summary>
internal sealed class BakeLightOwners
{
    private readonly List<ulong> _spaceLights = [];
    private readonly List<ulong> _spaceSuns = [];
    private ulong[] _sunHashes = [];
    private ulong[] _localHashes = [];
    private (LightKey Key, ulong Hash, bool Spot)[] _tracked = [];
    private int[] _owners = [];
    private ulong[] _chunkHashes = [];
    private int _count;
    private List<ulong>[] _chunkLights = [];

    /// <summary>For each local light, the area that owns it, or <see cref="BakeAreas.None"/>.</summary>
    public ReadOnlySpan<int> Owners => _owners.AsSpan(0, _count);

    /// <summary>For each local light a chunk owns, its hash in that chunk's space; 0 for any other light.</summary>
    public ReadOnlySpan<ulong> ChunkHashes => _chunkHashes.AsSpan(0, _count);

    /// <summary>The hashes of the lights chunk <paramref name="chunk"/> owns, each in the chunk's space.</summary>
    public ReadOnlySpan<ulong> ChunkLights(int chunk) => CollectionsMarshal.AsSpan(_chunkLights[chunk]);

    /// <summary>Forgets every light, as a frame that names no area does.</summary>
    public void Clear() => _count = 0;

    /// <summary>
    /// Gives every Mixed or Baked light's world-space hash to <paramref name="animated"/>, so a moving one is known
    /// before any area is hashed. Realtime lights are never hashed or tracked.
    /// </summary>
    public void Track(
        ReadOnlySpan<DirectionalLight> suns, ReadOnlySpan<LocalLight> locals, AnimatedLights animated, float time)
    {
        ReusedArrays.Grow(ref _sunHashes, suns.Length);
        ReusedArrays.Grow(ref _localHashes, locals.Length);
        ReusedArrays.Grow(ref _tracked, suns.Length + locals.Length);
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

        animated.Update(_tracked.AsSpan(0, trackedCount), time);
    }

    /// <summary>
    /// Works out which area owns each local light that can join a bake: <see cref="BakeAreas.None"/> for a Realtime or
    /// moving one. A chunk's lights are hashed in its own space, so every placed copy gives the same hash.
    /// </summary>
    public void Assign(
        ReadOnlySpan<LocalLight> locals, BakedSpace? space, ReadOnlySpan<BakedChunk> chunks, AnimatedLights animated)
    {
        ReusedArrays.ClearLists(ref _chunkLights, chunks.Length);
        ReusedArrays.Grow(ref _owners, locals.Length);
        ReusedArrays.Grow(ref _chunkHashes, locals.Length);
        _count = locals.Length;
        for (int i = 0; i < locals.Length; i++)
        {
            _owners[i] = InBake(locals[i].Mode, new LightKey(false, locals[i].Key), animated)
                ? BakeAreas.OwnerOfLight(locals[i].Position, space, chunks)
                : BakeAreas.None;
            _chunkHashes[i] = 0;
            if (_owners[i] >= 0)
            {
                _chunkHashes[i] = BakeInputHash.Light(locals[i], chunks[_owners[i]].Transform);
                _chunkLights[_owners[i]].Add(_chunkHashes[i]);
            }
        }
    }

    /// <summary>The world-space hashes of the local lights the space owns.</summary>
    public ReadOnlySpan<ulong> SpaceLights()
    {
        _spaceLights.Clear();
        for (int i = 0; i < _count; i++)
        {
            if (_owners[i] == BakeAreas.Space)
            {
                _spaceLights.Add(_localHashes[i]);
            }
        }

        return CollectionsMarshal.AsSpan(_spaceLights);
    }

    /// <summary>
    /// The hash of the suns in the space's bake, or null for none. One sun is hashed as itself; more than one, which
    /// is rare, as their count and hashes in ascending order.
    /// </summary>
    public ulong? SpaceSuns(ReadOnlySpan<DirectionalLight> suns, AnimatedLights animated)
    {
        _spaceSuns.Clear();
        for (int i = 0; i < suns.Length; i++)
        {
            if (InBake(suns[i].Mode, new LightKey(true, i), animated))
            {
                _spaceSuns.Add(_sunHashes[i]);
            }
        }

        if (_spaceSuns.Count <= 1)
        {
            return _spaceSuns.Count == 0 ? null : _spaceSuns[0];
        }

        _spaceSuns.Sort();
        var hasher = BakeInputHash.Hasher().Add(_spaceSuns.Count);
        foreach (ulong sun in _spaceSuns)
        {
            hasher.Add(sun);
        }

        return hasher.Finish();
    }

    /// <summary>
    /// Tells <paramref name="frame"/> which lights its fresh bakes take over. A light over a fresh bake of the area that
    /// owns it keeps its mode, and a Baked one is left to the bake, or dimmed while it fades in.
    /// </summary>
    public void HandOver(
        BakeFrame frame, ReadOnlySpan<DirectionalLight> suns, ReadOnlySpan<LocalLight> locals, AnimatedLights animated)
    {
        if (frame.SpaceFresh)
        {
            HandOverSuns(frame, suns, animated);
        }

        for (int i = 0; i < locals.Length; i++)
        {
            int owner = _owners[i];
            bool fresh = owner == BakeAreas.Space ? frame.SpaceFresh : owner >= 0 && frame.ChunkFresh[owner];
            if (!fresh)
            {
                continue;
            }

            frame.SetMode(new LightKey(false, locals[i].Key), locals[i].Mode);
            if (locals[i].Mode == LightMode.Baked)
            {
                frame.DropLocalLight(i, owner == BakeAreas.Space ? frame.SpaceFade : frame.ChunkFade(owner));
            }
        }
    }

    // Every sun lights the space, so each one in its bake is handed over.
    private static void HandOverSuns(BakeFrame frame, ReadOnlySpan<DirectionalLight> suns, AnimatedLights animated)
    {
        for (int i = 0; i < suns.Length; i++)
        {
            var key = new LightKey(true, i);
            if (InBake(suns[i].Mode, key, animated))
            {
                frame.SetMode(key, suns[i].Mode);
                if (suns[i].Mode == LightMode.Baked)
                {
                    frame.DropSun(i, frame.SpaceFade);
                }
            }
        }
    }

    // Whether a light can join a bake: neither Realtime nor moving.
    private static bool InBake(LightMode mode, LightKey key, AnimatedLights animated) =>
        mode != LightMode.Realtime && !animated.IsAnimated(key);
}
