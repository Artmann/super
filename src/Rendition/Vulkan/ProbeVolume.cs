using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The light probes' data on the GPU. Each probe has a tile in two atlases: the light reaching
/// it from every direction, and the mean and mean square of the distance it sees in every
/// direction, which lets a surface tell whether a probe is behind a wall from it. Each atlas is a
/// 2D array of five layers: the four cascades that follow the camera, finest first, then the fixed
/// grid in <see cref="FixedGridLayer"/>. A frame's probe update draws each probe's six faces into
/// its slot of the <see cref="Faces"/>, <see cref="RecordFold"/> folds them into a staging slot of the
/// <see cref="Atlases"/>, and <see cref="RecordBlend"/> blends a batch of slots into the atlases.
///
/// Nothing real is made until <see cref="EnsureStorage"/>: until then the atlases are a one-tile
/// stand-in, so a renderer with the probes switched off holds almost nothing.
/// See "Light probes" in docs/design.md.
/// </summary>
internal sealed unsafe class ProbeVolume : IDisposable
{
    /// <summary>How many texels across each captured face is. The probes keep only soft light, so this is plenty.</summary>
    public const uint FaceSize = ProbeFaces.FaceSize;

    /// <summary>
    /// What a face's alpha holds where nothing was drawn: further than any probe looks. Must match
    /// SKY_MARK in probe_fold.comp.
    /// </summary>
    public const float SkyMark = 1e4f;

    /// <summary>Texels across a tile, without its border. Must match probes.glsl and probe_fold.glsl.</summary>
    public const int IrradianceTexels = ProbeVolumeAtlases.IrradianceTexels;
    public const int DepthTexels = ProbeVolumeAtlases.DepthTexels;

    /// <summary>The atlas layer of the fixed grid. Layers 0 to 3 are the cascades.</summary>
    public const int FixedGridLayer = ProbeVolumeAtlases.FixedGridLayer;

    /// <summary>The atlases' layers: four cascades and the fixed grid.</summary>
    public const int Layers = ProbeVolumeAtlases.Layers;

    /// <summary>One probe's two bordered tiles in the staging buffer. See <see cref="ProbeVolumeAtlases.StagedTileBytes"/>.</summary>
    public const int StagedTileBytes = ProbeVolumeAtlases.StagedTileBytes;

    /// <summary>The faces' colour: the light seen in rgb and the distance in alpha.</summary>
    public const Format FacesFormat = ProbeFaces.ColorFormat;

    private readonly VulkanContext _context;
    private readonly ProbeVolumePasses _passes;

    /// <summary>
    /// Whether this GPU can update the probes. The depth atlas has two channels, which compute shaders
    /// can only write with shaderStorageImageExtendedFormats, and a probe's six faces are drawn at once
    /// with multiview. Without both the atlases are still bound, but nothing is captured and surfaces use the sky.
    /// </summary>
    public readonly bool Supported;

    /// <summary>
    /// Makes the stand-in atlases. <paramref name="sky"/> is the sharp sky, which the fold reads where
    /// a probe saw nothing.
    /// </summary>
    public ProbeVolume((VulkanContext Context, VulkanGpu Gpu) device, ImageView sky)
    {
        var context = device.Context;
        _context = context;
        Supported = context.Abilities.StorageImageExtendedFormats && context.Abilities.Multiview;
        _passes = new ProbeVolumePasses(device.Gpu, sky, Supported);
        Atlases = Placeholder();
    }

    /// <summary>
    /// The atlases, the staging slots the folds write and the ids the blend reads. One tile per layer and one
    /// staging slot until <see cref="EnsureStorage"/> makes the real ones.
    /// </summary>
    public ProbeVolumeAtlases Atlases { get; private set; }

    /// <summary>The faces a batch of probes is drawn into. Null until <see cref="EnsureStorage"/> makes them.</summary>
    public ProbeFaces? Faces { get; private set; }

    /// <summary>Whether the real atlases and faces exist, rather than the stand-in.</summary>
    public bool Allocated => Faces is not null;

    /// <summary>The quality the storage was made for. Only meaningful while <see cref="Allocated"/>.</summary>
    public ProbeQuality Quality { get; private set; }

    public ImageView IrradianceView => Atlases.IrradianceView;

    public ImageView DepthView => Atlases.DepthView;

    /// <summary>
    /// Makes the atlases, faces and staging for a quality, if they aren't already, with every probe
    /// uncaptured. Waits for the device when it makes them, so it is for the first frame with the
    /// probes on and for a change of quality, never every frame. Returns true when the views changed,
    /// so the caller must write its descriptors again.
    /// </summary>
    public bool EnsureStorage(ProbeQuality quality)
    {
        if (Allocated && quality == Quality)
        {
            return false;
        }

        _context.WaitIdle();
        DestroyStorage();
        Quality = quality;
        var budget = ProbeBudget.For(quality);
        Atlases = new ProbeVolumeAtlases(_context, ProbeVolumeAtlases.Grid(quality), budget.Traced, Supported);
        Faces = new ProbeFaces(_context, budget.Raster);
        if (Supported)
        {
            _passes.PointAt(Atlases, Faces.ArrayView);
        }

        return true;
    }

    /// <summary>
    /// Frees the atlases and faces and puts the stand-in back, for when the probes are switched off.
    /// Waits for the device. Returns true when there was anything to free, so the caller must write
    /// its descriptors again.
    /// </summary>
    public bool Release()
    {
        if (!Allocated)
        {
            return false;
        }

        _context.WaitIdle();
        DestroyStorage();
        Atlases = Placeholder();
        return true;
    }

    /// <summary>
    /// Makes the staging slots safe for <see cref="Passes.ProbeTracePass"/> to write, which runs outside the GPU layer:
    /// the last blend or read-back may still be reading them. The folds need no such call, as the layer orders them.
    /// </summary>
    public void BeginTraces(CommandBuffer cmd) =>
        _context.Api.MemoryBarrier(cmd,
            (PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit, AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit));

    /// <summary>
    /// Folds the six faces of one slot of the <see cref="Faces"/> into the same staging slot.
    /// <paramref name="spacing"/> is the probe's cascade's: further than two spacings a probe lights
    /// nothing, so distances stop there. Where it saw nothing it sees the sky: the environment times
    /// <paramref name="environmentIntensity"/>, whether or
    /// not the view shows the environment there, so a probe in the open reads as no probe does.
    /// </summary>
    public void RecordFold(CommandBuffer cmd, int slot, float spacing, float environmentIntensity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(slot, Math.Min(Atlases.StagingSlots, Faces?.Slots ?? 0));
        _passes.RecordFold(cmd, slot, spacing * 2f, environmentIntensity);
    }

    /// <summary>
    /// Copies the first <paramref name="slots"/> staging slots, as the folds recorded before it wrote them, into
    /// <paramref name="buffer"/>, <see cref="StagedTileBytes"/> each. The baker folds its traced faces this way and
    /// writes the tiles into its own atlases.
    /// </summary>
    public void RecordStagingReadBack(CommandBuffer cmd, GpuBuffer buffer, int slots)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slots);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(slots, Atlases.StagingSlots);
        _context.Api.MemoryBarrier(cmd,
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit));
        var region = new BufferCopy(0, 0, (ulong)slots * StagedTileBytes);
        _context.Api.CmdCopyBuffer(cmd, Atlases.Staging.Handle, buffer.Handle, 1, &region);
    }

    /// <summary>
    /// Makes uncaptured every probe of one atlas layer whose cell lies in the slab <paramref name="cells"/> names.
    /// The cells wrap to their slots as <see cref="ProbeCascades.Slot"/> does.
    /// </summary>
    public void RecordInvalidate(CommandBuffer cmd, in ProbeInvalidation cells)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cells.Layer);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(cells.Layer, Layers);
        int total = cells.Counts.X * cells.Counts.Y * cells.Counts.Z;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(total, Atlases.Columns * Atlases.Rows);
        _passes.RecordInvalidate(cmd, cells, total);
    }

    /// <summary>
    /// Blends staging slots 0 to batch.Count - 1 into the probes the batch names, in the same
    /// order: a probe never captured takes its new tile whole, and one already captured moves
    /// <paramref name="alpha"/> of the way towards it. With <paramref name="snap"/> above 0, a probe whose
    /// mean light changed by more moves further, all the way once the change is <paramref name="snap"/> of its
    /// light. Surfaces lit from the probes in this same command buffer, and in later ones, wait for it.
    /// Allocates nothing.
    /// </summary>
    public void RecordBlend(CommandBuffer cmd, IReadOnlyList<ProbeId> batch, float alpha, float snap = 0f)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(batch.Count, Atlases.StagingSlots);
        var api = _context.Api;

        // ids is two ints per probe, and StagingSlots keeps it well under CmdUpdateBuffer's 65,536 bytes.
        int count = batch.Count;
        int* ids = stackalloc int[count * 2];
        Atlases.PackIds(batch, ids);

        // The last blend may still be reading the ids.
        api.MemoryBarrier(cmd,
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit),
            (PipelineStageFlags2.AllTransferBit, AccessFlags2.TransferWriteBit));
        api.CmdUpdateBuffer(cmd, Atlases.Ids.Handle, 0, (ulong)(count * 8), ids);

        // The layer waits for the ids, the folds or copies that filled the staging slots and anything still reading
        // the atlases, and the draws that read them after wait for the blend.
        _passes.RecordBlend(cmd, count, alpha, snap);
    }

    public void Dispose()
    {
        DestroyStorage();
        _passes.Dispose();
    }

    // One tile per layer and one staging slot: enough to bind, and to keep the test hooks working.
    private ProbeVolumeAtlases Placeholder() => new(_context, (1, 1), 1, Supported);

    private void DestroyStorage()
    {
        Atlases.Dispose();
        Faces?.Dispose();
        Faces = null;
    }
}
