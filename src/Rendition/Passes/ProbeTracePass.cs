using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Updates a batch of light probes with rays, where the GPU can trace them: probe_trace.comp sends 128 rays from each
/// probe through the frame's top-level structure, shades what they hit as probe_light.glsl says, with shadow rays for
/// the sun and the point and spot lights, and folds them into the probe's tiles in <see cref="ProbeVolume.StagingTiles"/>,
/// slot i for probe i of the batch. The caller then blends them in. It takes the place of <see cref="ProbeCapturePass"/>
/// and the folds, and needs no sun shadow map. Glass and water are skipped by every ray, as the rasterized capture
/// leaves them out. Exists only where the device can trace. See "Light probes" in docs/design.md.
/// </summary>
internal sealed unsafe class ProbeTracePass : IDisposable
{
    /// <summary>One ray per invocation, and one workgroup per probe. Must match RAYS in probe_trace.comp.</summary>
    public const int Rays = 128;

    private const uint IrradianceBinding = 11;
    private const uint DepthBinding = 12;
    private const uint StagingBinding = 13;
    private const uint SkyBinding = 14;

    // Must match TracedProbe in probe_trace.comp.
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuTracedProbe
    {
        public Vector4 PositionSpacing;
        public uint Cascade;
        public uint Slot;
        public uint Unused0;
        public uint Unused1;
    }

    // Must match the push block in probe_trace.comp.
    [StructLayout(LayoutKind.Sequential)]
    private struct TraceConstants
    {
        public Vector4 SkySun;
        public uint Frame;
        public float EnvironmentIntensity;
        public uint Unused0;
        public uint Unused1;
    }

    private readonly VulkanContext _context;
    private readonly ResourceTable _resources;
    private readonly int _capacity;
    private readonly DescriptorSetLayout _probeLayout;
    private readonly DescriptorSetLayout _sceneLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _probeSet;
    private readonly DescriptorSet[] _sceneSets;
    private readonly GpuBuffer[] _probes;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _pipeline;

    /// <param name="framesInFlight">One scene set and one buffer of probes is kept per frame in flight.</param>
    /// <param name="frameSetLayout">Set 0: the frame's uniforms and lights.</param>
    /// <param name="resources">Set 1: the resource table's textures and materials.</param>
    /// <param name="capacity">The most probes one batch can have.</param>
    public ProbeTracePass(VulkanContext context, int framesInFlight, DescriptorSetLayout frameSetLayout, ResourceTable resources, int capacity)
    {
        _context = context;
        _resources = resources;
        _capacity = capacity;
        var api = context.Api;

        // Set 2: the atlases, for the bounce; the staging slots written; the sky, for the rays that hit nothing.
        var probeBindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(IrradianceBinding, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(DepthBinding, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(StagingBinding, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(SkyBinding, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
        };
        var probeLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 4,
            PBindings = probeBindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &probeLayoutInfo, null, out _probeLayout).Check();

        // Set 3: the frame's top-level structure, the geometry table its instances index, and the batch's probes.
        var sceneBindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.AccelerationStructureKhr, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(2, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
        };
        var sceneLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 3,
            PBindings = sceneBindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &sceneLayoutInfo, null, out _sceneLayout).Check();

        uint sets = (uint)framesInFlight + 1;
        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.CombinedImageSampler, 3),
            new(DescriptorType.StorageBuffer, 1 + 2 * (uint)framesInFlight),
            new(DescriptorType.AccelerationStructureKhr, (uint)framesInFlight),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = sets,
            PoolSizeCount = 3,
            PPoolSizes = poolSizes,
        };
        api.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool).Check();

        var probeLayout = _probeLayout;
        var probeAlloc = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &probeLayout,
        };
        api.AllocateDescriptorSets(context.Device, &probeAlloc, out _probeSet).Check();

        _sceneSets = new DescriptorSet[framesInFlight];
        var sceneLayouts = stackalloc DescriptorSetLayout[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
        {
            sceneLayouts[i] = _sceneLayout;
        }

        var sceneAlloc = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = (uint)framesInFlight,
            PSetLayouts = sceneLayouts,
        };
        fixed (DescriptorSet* allocated = _sceneSets)
        {
            api.AllocateDescriptorSets(context.Device, &sceneAlloc, allocated).Check();
        }

        _probes = new GpuBuffer[framesInFlight];
        for (int i = 0; i < framesInFlight; i++)
        {
            _probes[i] = GpuBuffer.CreateHostVisible(
                context, (ulong)(capacity * sizeof(GpuTracedProbe)), BufferUsageFlags.StorageBufferBit);
        }

        var pipelineSetLayouts = stackalloc DescriptorSetLayout[] { frameSetLayout, resources.Layout, _probeLayout, _sceneLayout };
        var pushRange = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(TraceConstants));
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 4,
            PSetLayouts = pipelineSetLayouts,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        api.CreatePipelineLayout(context.Device, &pipelineLayoutInfo, null, out _pipelineLayout).Check();
        _pipeline = PipelineFactory.CreateCompute(context, "probe_trace.comp", _pipelineLayout);
    }

    /// <summary>
    /// Points the pass at the probes' atlases, for the bounce, at their staging slots and at the sky. Call again
    /// whenever the atlases are made anew, with the device idle.
    /// </summary>
    public void WriteSet(DescriptorImageInfo irradiance, DescriptorImageInfo depth, Silk.NET.Vulkan.Buffer staging, DescriptorImageInfo sky)
    {
        var stagingInfo = new DescriptorBufferInfo(staging, 0, Vk.WholeSize);
        var writes = stackalloc WriteDescriptorSet[]
        {
            ImageWrite(IrradianceBinding, &irradiance),
            ImageWrite(DepthBinding, &depth),
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _probeSet,
                DstBinding = StagingBinding,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &stagingInfo,
            },
            ImageWrite(SkyBinding, &sky),
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 4, writes, 0, null);
    }

    /// <summary>
    /// Traces the batch's probes through <paramref name="scene"/> into staging slots 0 to batch.Count - 1, one
    /// workgroup each. <paramref name="positions"/> and <paramref name="spacings"/> are the batch's probes', in the same
    /// order. <paramref name="frame"/> seeds each probe's rotation of its rays, so the same frames give the same rays.
    /// <paramref name="tracing"/> built the scene earlier in <paramref name="cmd"/>; the frame in flight's set and buffer
    /// are rewritten, so only call it once that frame's last use is done. The caller makes the staging slots safe to
    /// write first (<see cref="ProbeVolume.BeginFolds"/>) and blends them after. A ray that hits nothing sees the
    /// environment times <paramref name="environmentIntensity"/>, with the clear sky of <paramref name="skySun"/>.
    /// </summary>
    public void Record(
        CommandBuffer cmd,
        IReadOnlyList<ProbeId> batch,
        in TopLevelStructure scene,
        RayTracing tracing,
        int frameIndex,
        DescriptorSet frameSet,
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<float> spacings,
        uint frame,
        float environmentIntensity,
        Vector4 skySun)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Count == 0)
        {
            return;
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(batch.Count, _capacity);
        if (positions.Length != batch.Count || spacings.Length != batch.Count)
        {
            throw new ArgumentException(
                $"The batch has {batch.Count} probes, but {positions.Length} positions and {spacings.Length} spacings.");
        }

        var probes = new Span<GpuTracedProbe>(_probes[frameIndex].Mapped, batch.Count);
        for (int i = 0; i < batch.Count; i++)
        {
            probes[i] = new GpuTracedProbe
            {
                PositionSpacing = new Vector4(positions[i], spacings[i]),
                Cascade = (uint)batch[i].Cascade,
                Slot = (uint)batch[i].Slot,
            };
        }

        var sceneSet = _sceneSets[frameIndex];
        WriteSceneSet(sceneSet, scene, _probes[frameIndex]);
        tracing.ReadyToTrace(cmd);

        var api = _context.Api;
        var constants = new TraceConstants
        {
            SkySun = skySun,
            Frame = frame,
            EnvironmentIntensity = environmentIntensity,
        };
        var sets = stackalloc DescriptorSet[] { frameSet, _resources.Set, _probeSet, sceneSet };
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 4, sets, 0, null);
        api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(TraceConstants), &constants);
        api.CmdDispatch(cmd, (uint)batch.Count, 1, 1);
    }

    private WriteDescriptorSet ImageWrite(uint binding, DescriptorImageInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = _probeSet,
        DstBinding = binding,
        DescriptorCount = 1,
        DescriptorType = DescriptorType.CombinedImageSampler,
        PImageInfo = info,
    };

    private void WriteSceneSet(DescriptorSet set, in TopLevelStructure scene, GpuBuffer probes)
    {
        var handle = scene.Handle;
        var structureInfo = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &handle,
        };
        var tableInfo = new DescriptorBufferInfo(scene.GeometryTable.Handle, 0, Vk.WholeSize);
        var probesInfo = new DescriptorBufferInfo(probes.Handle, 0, Vk.WholeSize);
        var writes = stackalloc WriteDescriptorSet[]
        {
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                PNext = &structureInfo,
                DstSet = set,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.AccelerationStructureKhr,
            },
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &tableInfo,
            },
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 2,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &probesInfo,
            },
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 3, writes, 0, null);
    }

    public void Dispose()
    {
        var api = _context.Api;
        api.DestroyPipeline(_context.Device, _pipeline, null);
        api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _sceneLayout, null);
        api.DestroyDescriptorSetLayout(_context.Device, _probeLayout, null);
        foreach (var buffer in _probes)
        {
            buffer.Dispose();
        }
    }
}
