using System.Numerics;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// A <see cref="BakeJob"/>'s steps for a space's probes, after its pages: a batch of
/// <see cref="BakeGpu.ProbesPerBatch"/> probes' passes at a time, then their folds, as many probes a fold as the volume
/// has slots for, each read back into the atlases.
/// </summary>
internal sealed unsafe class BakeProbeSteps
{
    private readonly BakeJobKit _kit;
    private readonly ProbeVolume? _volume;
    private readonly ProbeAtlases _atlases = new();
    private ProbeTargets? _targets;
    private int _firstProbe;
    private int _probePass;
    private int _row;
    private int _inGroup;

    /// <summary>Counts the probes and their paths, which <paramref name="kit"/>'s work then counts too.</summary>
    /// <exception cref="ArgumentException">The area holds more probes than the atlases do.</exception>
    public BakeProbeSteps(BakeJobKit kit, ProbeVolume? volume)
    {
        _kit = kit;
        _volume = volume;

        // A probe face texel sees one direction, not a cosine-weighted hemisphere, and the fold averages a thousand of
        // them into each tile texel, so a sixteenth of the passes gives as quiet a result.
        Passes = Math.Max(1, kit.Passes / 16);
        Count = kit.Area.Probes is { } probes ? (int)ProbeGrid.Total(probes) : 0;
        if (Count > ProbeGrid.MaxProbes)
        {
            throw new ArgumentException(
                $"The probes number {Count}, more than the {ProbeGrid.MaxProbes} the atlases hold.", "area");
        }

        var extent = BakeProbePass.Extent;
        int batches = (Count + BakeGpu.ProbesPerBatch - 1) / BakeGpu.ProbesPerBatch;
        kit.TotalWork += (double)extent.Width * extent.Height * Passes * batches;
    }

    /// <summary>The probes the area holds.</summary>
    public int Count { get; }

    /// <summary>The passes each probe's paths take.</summary>
    public int Passes { get; }

    /// <summary>A slice of a batch of probes' passes, after clearing the batch's sums.</summary>
    public BakeStep Trace(CommandBuffer cmd, int passes)
    {
        var baker = _kit.Baker;
        var targets = _targets ??= new ProbeTargets(_kit, _volume!, Count);
        if (_probePass == 0)
        {
            baker.Tracing.ClearSums(cmd, [targets.Sum, targets.Faces]);
        }

        var constants = new ProbeTraceConstants
        {
            FirstProbe = (uint)_firstProbe,
            ProbeCount = (uint)Count,
            Passes = (uint)Passes,
            LightCount = (uint)_kit.Area.Lights.Length,
            Lift = BakeGpu.TraceLift,
            SkyIntensity = _kit.Area.SkyIntensity,
            MaxDistance = _kit.Area.Probes!.Value.Spacing * 2f,
        };
        var extent = BakeProbePass.Extent;
        int count = Math.Min(passes, Passes - _probePass);
        baker.Probes.RecordPasses(cmd, targets.Set, constants, _probePass, count);
        _probePass += count;
        _kit.PassesInLastStep = count;
        _kit.DoneWork += (double)extent.Width * extent.Height * count;
        if (_probePass == Passes)
        {
            _row = 0;
            return new BakeStep(BakeStepResult.Continue, BakeStage.Fold);
        }

        return new BakeStep(BakeStepResult.Continue, BakeStage.ProbeTrace);
    }

    /// <summary>
    /// The live fold of as many of the batch's probes as the volume has slots for, and the copy of their tiles. The
    /// traced distances are already clamped short of the fold's sky mark, so it keeps the sky the paths brought back.
    /// </summary>
    public BakeStep Fold(CommandBuffer cmd)
    {
        var volume = _volume!;
        var targets = _targets!;
        int slots = Math.Min(volume.FaceSlots, volume.StagingSlots);
        _inGroup = Math.Min(slots, InBatch() - _row);
        _kit.Baker.Probes.RecordCopyFaces(cmd, targets.Faces, _row, _inGroup);
        volume.BeginFolds(cmd);
        for (int slot = 0; slot < _inGroup; slot++)
        {
            volume.RecordFold(cmd, slot, _kit.Area.Probes!.Value.Spacing, _kit.Area.SkyIntensity);
        }

        volume.RecordStagingReadBack(cmd, targets.FoldBuffer, _inGroup);
        return new BakeStep(BakeStepResult.AwaitGpu, BakeStage.ReadFold);
    }

    /// <summary>Writes the folded tiles into the atlases, then folds the next group, traces the next batch, or ends the bake.</summary>
    public BakeStep ReadFold()
    {
        var targets = _targets!;
        for (int slot = 0; slot < _inGroup; slot++)
        {
            var tile = new ReadOnlySpan<float>(
                (byte*)targets.FoldBuffer.Mapped + (long)slot * ProbeVolume.StagedTileBytes,
                ProbeVolume.StagedTileBytes / sizeof(float));
            BakeEncoding.WriteTile(tile, _firstProbe + _row + slot, _atlases.Irradiance, _atlases.Depth);
        }

        _row += _inGroup;
        if (_row < InBatch())
        {
            return new BakeStep(BakeStepResult.Continue, BakeStage.Fold);
        }

        _firstProbe += BakeGpu.ProbesPerBatch;
        _probePass = 0;
        if (_firstProbe < Count)
        {
            return new BakeStep(BakeStepResult.Continue, BakeStage.ProbeTrace);
        }

        Free();
        return new BakeStep(BakeStepResult.Done, BakeStage.Done);
    }

    /// <summary>The probe atlases as the file stores them.</summary>
    public (LightBakePage Irradiance, LightBakePage Depth) Pages() => _atlases.Pages();

    /// <summary>Frees the batch's targets and buffers.</summary>
    public void Free()
    {
        _targets?.Free();
        _targets = null;
    }

    // The probes in the batch being baked.
    private int InBatch() => Math.Min(BakeGpu.ProbesPerBatch, Count - _firstProbe);
}

/// <summary>
/// What every batch of a bake's probes shares: their positions, the sum and faces the passes write, the probe set, and
/// the buffer the folded tiles are read back into.
/// </summary>
internal sealed unsafe class ProbeTargets
{
    /// <summary>Uploads every probe's position and makes the targets, descriptor set and readback buffer.</summary>
    public ProbeTargets(BakeJobKit kit, ProbeVolume volume, int count)
    {
        var context = kit.Context;
        var settings = kit.Area.Probes!.Value;
        Positions = GpuBuffer.CreateHostVisible(
            context, (ulong)count * (ulong)sizeof(Vector4), BufferUsageFlags.StorageBufferBit);
        var positions = new Span<Vector4>(Positions.Mapped, count);
        for (int i = 0; i < count; i++)
        {
            positions[i] = new Vector4(ProbeGrid.Position(settings, i), 1f);
        }

        (Sum, Faces) = kit.Baker.Probes.CreateTargets();
        Set = kit.Baker.Probes.CreateSet(kit.Pool, kit.Inputs, Positions, (Sum, Faces));
        int slots = Math.Min(volume.FaceSlots, volume.StagingSlots);
        FoldBuffer = GpuBuffer.CreateHostVisible(
            context, (ulong)slots * ProbeVolume.StagedTileBytes, BufferUsageFlags.TransferDstBit);
    }

    public GpuBuffer Positions { get; }

    public RenderTarget Sum { get; }

    public RenderTarget Faces { get; }

    public DescriptorSet Set { get; }

    public GpuBuffer FoldBuffer { get; }

    public void Free()
    {
        Sum.Dispose();
        Faces.Dispose();
        Positions.Dispose();
        FoldBuffer.Dispose();
    }
}

/// <summary>A bake's probe irradiance and depth atlases, in the layout of <see cref="BakedProbeAtlas"/>, made as first written.</summary>
internal sealed class ProbeAtlases
{
    private byte[]? _irradiance;
    private byte[]? _depth;

    public byte[] Irradiance =>
        _irradiance ??= new byte[BakedProbeAtlas.IrradianceAtlasSize * BakedProbeAtlas.IrradianceAtlasSize * 8];

    public byte[] Depth =>
        _depth ??= new byte[BakedProbeAtlas.DepthAtlasSize * BakedProbeAtlas.DepthAtlasSize * 8];

    /// <summary>The two atlases as the file's pages.</summary>
    public (LightBakePage Irradiance, LightBakePage Depth) Pages()
    {
        const int irradianceSide = BakedProbeAtlas.IrradianceAtlasSize;
        const int depthSide = BakedProbeAtlas.DepthAtlasSize;
        return (new LightBakePage(irradianceSide, irradianceSide, LightBakePageFormat.Rgba16f, Irradiance),
            new LightBakePage(depthSide, depthSide, LightBakePageFormat.Rgba16f, Depth));
    }
}
