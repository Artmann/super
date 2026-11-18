using System.Numerics;

namespace Rendition;

/// <summary>
/// The frame's four shadow mask slots while it draws a bake. Gives each of the frame's Mixed lights with a channel in its
/// own bake one of the slots, with <see cref="MaskSlots.Assign"/>. The space's first Mixed sun keeps slot 0, as
/// <see cref="ShadowMaskChannels.Assign"/> gave it at bake time, and the space's local lights keep their channels.
/// Each relit chunk's Mixed lights, found in its bake by their chunk-space hash, take the slots left, and the chunk's
/// table says which of its channels each slot is. A light only counts while it is still Mixed in its bake, so one
/// turned Realtime never reads it. See "Shadow mask channels" in docs/design.md.
/// </summary>
internal sealed class FrameMaskSlots
{
    // MaskSlots.Assign's scratch, grown when a frame has more lights, and which chunk and channel each chunk light is.
    private (int Key, int SpaceChannel)[] _spaceLights = [];
    private (int Key, int Chunk, Vector3 Position, float Range)[] _chunkLights = [];
    private (int Chunk, int Channel)[] _chunkOf = [];
    private int[] _slots = [];
    private int _spaceCount;
    private int _chunkCount;

    /// <summary>The index, among the suns drawn, of the space's Mixed sun whose shadow is in slot 0, or -1.</summary>
    public int Sun { get; private set; } = -1;

    /// <summary>
    /// The frame's mask slot of each Mixed local light that has one, by key. A space mask's unused channels hold 0, so
    /// a light missing here must never read it.
    /// </summary>
    public Dictionary<int, int> Channels { get; } = [];

    /// <summary>
    /// The space bake's channel for each of the four slots: itself, or -1 where a chunk light took the slot, so that
    /// light reads 1 on the space's surfaces. Written at the head of the chunk buffer every frame.
    /// </summary>
    public int[] SpaceSlots { get; } = [0, 1, 2, 3];

    /// <summary>Gives every slot back to the space, with no sun and no light reading one.</summary>
    public void Reset()
    {
        Sun = -1;
        Channels.Clear();
        for (int s = 0; s < SpaceSlots.Length; s++)
        {
            SpaceSlots[s] = s;
        }
    }

    /// <summary>Starts a frame's lists, with room for each of its <paramref name="localCount"/> local lights.</summary>
    public void Begin(int localCount)
    {
        if (_spaceLights.Length < localCount)
        {
            _spaceLights = new (int, int)[localCount];
            _chunkLights = new (int, int, Vector3, float)[localCount];
            _chunkOf = new (int, int)[localCount];
            _slots = new int[2 * localCount];
        }

        _spaceCount = 0;
        _chunkCount = 0;
    }

    /// <summary>
    /// Takes the drawn space's first Mixed sun, and lists its bake's local lights that are still Mixed, with the
    /// channels the bake gave them.
    /// </summary>
    public void AddSpace(int sunCount, BakeFrame bakes, Dictionary<int, int>? channels)
    {
        Sun = MixedSun(sunCount, bakes);
        if (channels is null)
        {
            return;
        }

        foreach (var (key, channel) in channels)
        {
            if (_spaceCount < _spaceLights.Length && bakes.ModeOf(new LightKey(false, key)) == LightMode.Mixed)
            {
                _spaceLights[_spaceCount++] = (key, channel);
            }
        }
    }

    /// <summary>
    /// Lists each relit chunk's local lights that are still Mixed and have a channel in its bake, found by their
    /// chunk-space hash. <paramref name="relit"/> is the pool entry of each chunk the frame relights, or null.
    /// </summary>
    public void AddChunks(
        ReadOnlySpan<LocalLight> locals, BakeFrame bakes, BakeResolver resolver, ReadOnlySpan<ChunkBakePool.Entry?> relit)
    {
        var owners = resolver.LightOwners;
        var hashes = resolver.LightChunkHashes;
        for (int i = 0; i < owners.Length && i < locals.Length; i++)
        {
            int c = owners[i];
            if (c < 0 || relit[c] is not { } entry
                || bakes.ModeOf(new LightKey(false, locals[i].Key)) != LightMode.Mixed
                || !TryChunkChannel(entry, hashes[i], out int channel))
            {
                continue;
            }

            _chunkLights[_chunkCount] = (locals[i].Key, c, locals[i].Position, locals[i].Range);
            _chunkOf[_chunkCount] = (c, channel);
            _chunkCount++;
        }
    }

    /// <summary>
    /// Gives the listed lights their slots with <see cref="MaskSlots.Assign"/>, and tells each relit chunk in
    /// <paramref name="chunks"/> which of its channels its slots are.
    /// </summary>
    public void Assign(BakeFrame bakes, Span<GpuChunk> chunks)
    {
        var slots = _slots.AsSpan(0, _spaceCount + _chunkCount);
        MaskSlots.Assign(Sun >= 0 ? 0 : -1, _spaceLights.AsSpan(0, _spaceCount),
            _chunkLights.AsSpan(0, _chunkCount), slots, bakes.LightUse.Unslotted);

        for (int i = 0; i < _spaceCount; i++)
        {
            Channels[_spaceLights[i].Key] = slots[i];
        }

        for (int i = 0; i < _chunkCount; i++)
        {
            int slot = slots[_spaceCount + i];
            if (slot < 0)
            {
                continue;
            }

            var (c, channel) = _chunkOf[i];
            Channels[_chunkLights[i].Key] = slot;
            SetMaskSlot(ref chunks[c], slot, channel);
            SpaceSlots[slot] = -1;
        }
    }

    /// <summary>
    /// The index, among the suns drawn, of the space's first Mixed sun, or -1. The suns drawn leave out the Baked ones,
    /// so its index moves down past those before it.
    /// </summary>
    private static int MixedSun(int sunCount, BakeFrame bakes)
    {
        for (int i = 0; i < sunCount; i++)
        {
            if (bakes.ModeOf(new LightKey(true, i)) != LightMode.Mixed)
            {
                continue;
            }

            // Indexed, since a foreach over the IReadOnlyList would box its enumerator every frame.
            int dropped = 0;
            var droppedSuns = bakes.DroppedSuns;
            for (int d = 0; d < droppedSuns.Count; d++)
            {
                if (droppedSuns[d] < i)
                {
                    dropped++;
                }
            }

            return i - dropped;
        }

        return -1;
    }

    // The channel the chunk's bake gave the light with this chunk-space hash, if it gave it one.
    private static bool TryChunkChannel(ChunkBakePool.Entry entry, ulong lightHash, out int channel)
    {
        foreach (var (hash, bakedChannel) in entry.Channels)
        {
            if (hash == lightHash)
            {
                channel = bakedChannel;
                return true;
            }
        }

        channel = -1;
        return false;
    }

    // Says that the frame's mask slot is the chunk bake's channel.
    private static void SetMaskSlot(ref GpuChunk chunk, int slot, int channel)
    {
        switch (slot)
        {
            case 0:
                chunk.Mask0 = channel;
                break;
            case 1:
                chunk.Mask1 = channel;
                break;
            case 2:
                chunk.Mask2 = channel;
                break;
            default:
                chunk.Mask3 = channel;
                break;
        }
    }
}
