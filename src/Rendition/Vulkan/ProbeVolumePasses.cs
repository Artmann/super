using System.Runtime.InteropServices;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// One atlas layer's cells to make uncaptured: the layer's box runs from <paramref name="Origin"/> for
/// <paramref name="Counts"/> cells, and the cells to clear are those in the slab from <paramref name="SlabStart"/> for
/// <paramref name="SlabCount"/> along any axis. A slab with no cells is (0, 0).
/// </summary>
internal readonly record struct ProbeInvalidation(int Layer, Int3 Origin, Int3 Counts, Int3 SlabStart, Int3 SlabCount);

/// <summary>
/// The light probes' three compute passes, run through the GPU layer: probe_fold.comp folds a probe's six faces into a
/// staging slot, probe_blend.comp blends staging slots into the atlases, and probe_invalidate.comp makes a slab of
/// cells uncaptured. The layer's barriers order them against each other and against the draws that read the atlases.
/// Without <c>supported</c> there are no pipelines, and nothing may be recorded.
/// </summary>
internal sealed class ProbeVolumePasses : IDisposable
{
    /// <summary>Must match the Fold block in Shaders/probe_fold.comp.</summary>
    private struct FoldConstants
    {
        public int Slot;
        public float MaxDistance;
        public float EnvironmentIntensity;
    }

    /// <summary>Must match the Blend block in Shaders/probe_blend.comp.</summary>
    private struct BlendConstants
    {
        public float Alpha;
        public int Count;
        public float Snap;
    }

    /// <summary>Must match the Invalidate block in Shaders/probe_invalidate.comp, whose four ivec4s each end in a w.</summary>
    private struct InvalidateConstants
    {
        public int OriginX, OriginY, OriginZ, Layer;
        public int CountX, CountY, CountZ, Total;
        public int StartX, StartY, StartZ, StartW;
        public int SlabX, SlabY, SlabZ, SlabW;
    }

    private readonly VulkanGpu _gpu;
    private readonly (SamplerId Sampler, ImageView View) _sky;
    private readonly (ComputePass Fold, ComputePass Blend, ComputePass Invalidate)? _passes;
    private readonly (ImageGroup Fold, ImageGroup Blend, ImageGroup Invalidate) _groups;
    private readonly CommandList _commands = new();
    private (BufferId Staging, BufferId Ids)? _buffers;

    /// <summary>The passes, reading <paramref name="sky"/>'s sharpest level where a probe saw nothing.</summary>
    public ProbeVolumePasses(VulkanGpu gpu, ImageView sky, bool supported)
    {
        _gpu = gpu;
        _sky = (gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear)), sky);

        // The fold reads the faces texel by texel.
        var faces = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Nearest));
        _groups = (new ImageGroup(gpu, faces), new ImageGroup(gpu, faces), new ImageGroup(gpu, faces));
        if (supported)
        {
            _passes = (new ComputePass(gpu, "probe_fold.comp"), new ComputePass(gpu, "probe_blend.comp"),
                new ComputePass(gpu, "probe_invalidate.comp"));
        }
    }

    /// <summary>
    /// Points the passes at the faces the fold reads and the atlases, staging slots and ids they use. The GPU must have
    /// finished every pass recorded before.
    /// </summary>
    public void PointAt(ProbeVolumeAtlases atlases, ImageView faces)
    {
        var passes = _passes ?? throw new InvalidOperationException("This GPU can't update the probes.");
        ReleaseBuffers();
        var staging = _gpu.Adopt(atlases.Staging.Handle, (long)atlases.Staging.Size);
        var ids = _gpu.Adopt(atlases.Ids.Handle, (long)atlases.Ids.Size);
        _buffers = (staging, ids);

        var irradiance = new GroupImage("irradianceAtlas", atlases.IrradianceView, Use: GroupImageUse.Written);
        _groups.Fold.Make(passes.Fold.Pipeline, 0, [("faces", faces), new GroupImage("sky", _sky.View, _sky.Sampler)],
            [("StagingTiles", staging)]);
        _groups.Blend.Make(passes.Blend.Pipeline, 0,
            [irradiance, new GroupImage("depthAtlas", atlases.DepthView, Use: GroupImageUse.Written)],
            [("StagingTiles", staging), ("BatchIds", ids)]);
        _groups.Invalidate.Make(passes.Invalidate.Pipeline, 0, [irradiance]);
    }

    /// <summary>Folds the six faces of one slot into the same staging slot, with distances stopping at <paramref name="maxDistance"/>.</summary>
    public void RecordFold(CommandBuffer cmd, int slot, float maxDistance, float environmentIntensity)
    {
        var constants = new FoldConstants { Slot = slot, MaxDistance = maxDistance, EnvironmentIntensity = environmentIntensity };
        Dispatch(cmd, (_passes!.Value.Fold, _groups.Fold), MemoryMarshal.AsBytes(new ReadOnlySpan<FoldConstants>(in constants)), 1);
    }

    /// <summary>Makes uncaptured the cells of a slab of one layer, <paramref name="total"/> cells in the layer's box.</summary>
    public void RecordInvalidate(CommandBuffer cmd, in ProbeInvalidation cells, int total)
    {
        var constants = new InvalidateConstants
        {
            OriginX = cells.Origin.X, OriginY = cells.Origin.Y, OriginZ = cells.Origin.Z, Layer = cells.Layer,
            CountX = cells.Counts.X, CountY = cells.Counts.Y, CountZ = cells.Counts.Z, Total = total,
            StartX = cells.SlabStart.X, StartY = cells.SlabStart.Y, StartZ = cells.SlabStart.Z, StartW = 0,
            SlabX = cells.SlabCount.X, SlabY = cells.SlabCount.Y, SlabZ = cells.SlabCount.Z, SlabW = 0,
        };
        Dispatch(cmd, (_passes!.Value.Invalidate, _groups.Invalidate),
            MemoryMarshal.AsBytes(new ReadOnlySpan<InvalidateConstants>(in constants)), (total + 63) / 64);
    }

    /// <summary>Blends the first <paramref name="count"/> staging slots into the probes the ids name.</summary>
    public void RecordBlend(CommandBuffer cmd, int count, float alpha, float snap)
    {
        var constants = new BlendConstants { Alpha = alpha, Count = count, Snap = snap };
        Dispatch(cmd, (_passes!.Value.Blend, _groups.Blend), MemoryMarshal.AsBytes(new ReadOnlySpan<BlendConstants>(in constants)), count);
    }

    public void Dispose()
    {
        _groups.Fold.Dispose();
        _groups.Blend.Dispose();
        _groups.Invalidate.Dispose();
        ReleaseBuffers();
        if (_passes is { } passes)
        {
            passes.Fold.Dispose();
            passes.Blend.Dispose();
            passes.Invalidate.Dispose();
        }
    }

    private void ReleaseBuffers()
    {
        if (_buffers is { } buffers)
        {
            _gpu.Release(buffers.Staging);
            _gpu.Release(buffers.Ids);
            _buffers = null;
        }
    }

    private void Dispatch(CommandBuffer cmd, (ComputePass Pass, ImageGroup Group) pass, ReadOnlySpan<byte> constants, int groups)
    {
        _commands.Reset();
        pass.Pass.Record(_commands, [pass.Group.Id], constants, (groups, 1, 1));
        _gpu.Play(_commands, cmd);
    }
}
