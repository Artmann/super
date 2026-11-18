using System.Numerics;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// The placed chunks a frame relights by its own sky and suns: each fresh one whose bake is on the GPU with its transfer
/// pages. Keeps their <see cref="GpuChunk"/> data, the suns' coefficients and the chunk each draw is relit as, and writes
/// them into the frame's chunk buffer. See "Chunk transfer" in docs/design.md.
/// </summary>
internal sealed unsafe class ChunkRelight(ChunkBakePool pool, ReadableBakes readable)
{
    /// <summary>The suns' four coefficients ahead of the chunks in each frame's chunk buffer.</summary>
    public const int SunShBytes = 4 * 16;

    /// <summary>
    /// The suns, then the space's table from the frame's mask slots to its bake's channels, four ints, and then the
    /// chunks.
    /// </summary>
    public const int HeaderBytes = SunShBytes + 4 * sizeof(int);

    private readonly GpuChunk[] _chunkData = new GpuChunk[BakeFiles.MaxChunks];
    private readonly Vector4[] _sunSh = new Vector4[4];

    // The pool entry of each chunk this frame relights, or null.
    private readonly ChunkBakePool.Entry?[] _relitEntries = new ChunkBakePool.Entry?[BakeFiles.MaxChunks];
    private int _chunkCount;

    /// <summary>Whether this frame relights any chunk. A frame with none is drawn as before.</summary>
    public bool Active { get; private set; }

    /// <summary>Whether a relit chunk's bake is still fading in.</summary>
    public bool Fading { get; private set; }

    /// <summary>The lightmap rectangles of each chunk this frame relights, by bake id, or null. A draw reads only its own chunk's.</summary>
    public IReadOnlyDictionary<ulong, GpuLightmapRect>?[] Lightmaps { get; } =
        new IReadOnlyDictionary<ulong, GpuLightmapRect>?[BakeFiles.MaxChunks];

    /// <summary>Each draw's chunk, -1 for a draw no relit chunk owns. Only filled while <see cref="Active"/>.</summary>
    public int[] DrawChunks { get; private set; } = [];

    /// <summary>Relights no chunk, for a frame that names no baked area.</summary>
    public void Forget() => Active = false;

    /// <summary>
    /// Works out which placed chunks this frame relights, and fills their data, the suns' coefficients and the chunk
    /// each draw is relit as, from <paramref name="resolver"/>'s owners.
    /// </summary>
    public void Find(in FrameView view, int drawCount, BakeFrame bakes, BakeResolver resolver)
    {
        Active = false;
        Fading = false;
        _chunkCount = 0;
        var chunks = view.BakedChunks;
        var store = readable.Inner;
        if (chunks.IsEmpty || !readable.GivesLightmaps(store))
        {
            return;
        }

        for (int c = 0; c < chunks.Length; c++)
        {
            _relitEntries[c] = null;
            Lightmaps[c] = null;
            // A chunk not relit keeps last frame's _chunkData, which is harmless: its draws get chunk -1, so no shader reads it.
            if (!bakes.ChunkFresh[c] || pool.Use(bakes.ChunkFile(c), store) is not { IsResident: true } entry
                || entry.Transfer[0] < 0)
            {
                continue;
            }

            _relitEntries[c] = entry;
            Lightmaps[c] = entry.Rects;
            Active = true;
            Fading |= bakes.ChunkFade(c) < 1f;
            _chunkData[c] = RelitChunk(chunks[c], entry, bakes.ChunkFade(c));
        }

        if (!Active)
        {
            return;
        }

        _chunkCount = chunks.Length;
        FindDrawChunks(drawCount, resolver.Owners);
        ProjectBakedSuns(view.Lights);
    }

    /// <summary>
    /// Gives the frame's Mixed lights their mask slots: the space's, when <paramref name="spaceChannels"/> is the drawn
    /// space bake's, and the relit chunks'. See <see cref="FrameMaskSlots"/>.
    /// </summary>
    public void AssignMaskSlots(
        FrameMaskSlots slots, in FrameView view, BakeFrame bakes, Dictionary<int, int>? spaceChannels, BakeResolver resolver)
    {
        slots.Begin(view.LocalLights.Length);
        if (spaceChannels is not null)
        {
            slots.AddSpace(view.Lights.Length, bakes, spaceChannels);
        }

        if (Active)
        {
            slots.AddChunks(view.LocalLights, bakes, resolver, _relitEntries);
        }

        slots.Assign(bakes, _chunkData);
    }

    /// <summary>
    /// Writes the space's mask slots into a frame's chunk buffer, and while chunks are relit, the suns' coefficients
    /// and the chunks.
    /// </summary>
    public void WriteTo(GpuBuffer chunks, int[] spaceSlots)
    {
        // The space's slots go every frame, so a frame drawing only the space reads its mask as it always has.
        spaceSlots.AsSpan().CopyTo(new Span<int>((byte*)chunks.Mapped + SunShBytes, spaceSlots.Length));
        if (Active)
        {
            _sunSh.AsSpan().CopyTo(new Span<Vector4>(chunks.Mapped, _sunSh.Length));
            _chunkData.AsSpan(0, _chunkCount).CopyTo(new Span<GpuChunk>((byte*)chunks.Mapped + HeaderBytes, _chunkCount));
        }
    }

    /// <summary>
    /// A placed chunk's data for the shaders: the way into its space, its box, how far its bake has faded in, and the
    /// pages of its transfer and mask.
    /// </summary>
    private static GpuChunk RelitChunk(in BakedChunk chunk, ChunkBakePool.Entry entry, float fade)
    {
        var m = chunk.Transform;
        var t = m.Translation;
        var transfer = entry.Transfer;
        return new GpuChunk
        {
            // A chunk only turns and moves, so the way back is the turn's transpose after taking the move off.
            IntoX = new Vector4(m.M11, m.M12, m.M13, -(t.X * m.M11 + t.Y * m.M12 + t.Z * m.M13)),
            IntoY = new Vector4(m.M21, m.M22, m.M23, -(t.X * m.M21 + t.Y * m.M22 + t.Z * m.M23)),
            IntoZ = new Vector4(m.M31, m.M32, m.M33, -(t.X * m.M31 + t.Y * m.M32 + t.Z * m.M33)),
            Min = new Vector4(chunk.Min, chunk.EdgeBlend),
            Max = new Vector4(chunk.Max, fade),
            // No slot reads the mask until AssignMaskSlots gives the chunk's lights theirs.
            Mask0 = -1, Mask1 = -1, Mask2 = -1, Mask3 = -1,
            SkyRed = transfer[0], SkyGreen = transfer[1], SkyBlue = transfer[2], Sun = transfer[3],
            Tint = transfer[4],
            MaskPage = entry.MaskPage,
        };
    }

    // The chunk each draw is relit as, from the resolver's owners, or -1 for a draw no relit chunk owns.
    private void FindDrawChunks(int drawCount, ReadOnlySpan<int> owners)
    {
        if (DrawChunks.Length < drawCount)
        {
            DrawChunks = new int[drawCount * 2];
        }

        for (int d = 0; d < drawCount; d++)
        {
            int owner = d < owners.Length ? owners[d] : BakeAreas.None;
            DrawChunks[d] = owner >= 0 && _relitEntries[owner] is not null ? owner : -1;
        }
    }

    /// <summary>
    /// Projects every sun a bake's bounce can follow, as deltas: each one's radiance times the basis towards it.
    /// </summary>
    private void ProjectBakedSuns(ReadOnlySpan<DirectionalLight> suns)
    {
        _sunSh.AsSpan().Clear();
        foreach (var sun in suns)
        {
            if (sun.Mode == LightMode.Realtime)
            {
                continue;
            }

            var towards = Vector3.Normalize(sun.Direction);
            var radiance = new Vector4(sun.Radiance, 0f);
            _sunSh[0] += ShL1.Y0 * radiance;
            _sunSh[1] += ShL1.Y1 * towards.Y * radiance;
            _sunSh[2] += ShL1.Y1 * towards.Z * radiance;
            _sunSh[3] += ShL1.Y1 * towards.X * radiance;
        }
    }
}
