using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// The trace debug view (<see cref="DebugView.Trace"/>): trace_debug.comp sends a ray from the camera through every
/// pixel and writes the base colour of what it hits into the lit HDR target, in place of the lighting pass. Exists only
/// where the device can trace. See "Ray tracing" in docs/design.md.
/// </summary>
internal sealed unsafe class TraceDebugPass : IDisposable
{
    private const uint GroupSize = 8;

    private readonly VulkanContext _context;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet[] _sets;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _pipeline;

    /// <param name="framesInFlight">One set is kept per frame in flight, since each frame traces its own structure.</param>
    /// <param name="frameSetLayout">Set 0: the frame's uniforms.</param>
    /// <param name="resourceLayout">Set 2: the resource table's textures and materials.</param>
    public TraceDebugPass(VulkanContext context, int framesInFlight, DescriptorSetLayout frameSetLayout, DescriptorSetLayout resourceLayout)
    {
        _context = context;

        // The scene's top-level structure, the geometry table its instances index, and the target written.
        _setLayout = context.CreateSetLayout(
        [
            new(0, DescriptorType.AccelerationStructureKhr, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
            new(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
        ]);
        _pool = context.CreateDescriptorPool(
            (uint)framesInFlight,
            new DescriptorPoolSize(DescriptorType.AccelerationStructureKhr, (uint)framesInFlight),
            new DescriptorPoolSize(DescriptorType.StorageBuffer, (uint)framesInFlight),
            new DescriptorPoolSize(DescriptorType.StorageImage, (uint)framesInFlight));

        _sets = new DescriptorSet[framesInFlight];
        context.AllocateSets(_pool, _setLayout, _sets);

        _pipelineLayout = context.CreatePipelineLayout([frameSetLayout, _setLayout, resourceLayout]);
        _pipeline = PipelineFactory.CreateCompute(context, "trace_debug.comp", _pipelineLayout);
    }

    /// <summary>
    /// Traces <paramref name="scene"/> into <paramref name="target"/>, which must have been made with storage use, and
    /// leaves it ready to sample as the lighting pass would. <paramref name="tracing"/> built the scene earlier in
    /// <paramref name="cmd"/>; the frame in flight's set is rewritten, so only call it once that frame's last use is
    /// done.
    /// </summary>
    public void Record(
        CommandBuffer cmd,
        int frameIndex,
        RayTracing tracing,
        in TopLevelStructure scene,
        DescriptorSet frameSet,
        DescriptorSet resourceSet,
        RenderTarget target,
        Extent2D extent)
    {
        var api = _context.Api;
        var set = _sets[frameIndex];
        WriteSet(set, scene, target.View);

        tracing.ReadyToTrace(cmd);

        // Last frame sampled it, copied it and metered it; its contents are all replaced.
        api.Transition(cmd, target.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.General,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit,
            AccessFlags2.None,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit);

        var sets = stackalloc DescriptorSet[] { frameSet, set, resourceSet };
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 3, sets, 0, null);
        api.CmdDispatch(cmd, (extent.Width + GroupSize - 1) / GroupSize, (extent.Height + GroupSize - 1) / GroupSize, 1);

        api.Transition(cmd, target.Image, ImageAspectFlags.ColorBit,
            ImageLayout.General, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit);
    }

    private void WriteSet(DescriptorSet set, in TopLevelStructure scene, ImageView target)
    {
        var handle = scene.Handle;
        var structureInfo = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &handle,
        };
        var tableInfo = new DescriptorBufferInfo(scene.GeometryTable.Handle, 0, Vk.WholeSize);
        var imageInfo = new DescriptorImageInfo(default, target, ImageLayout.General);
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
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &imageInfo,
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
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
    }
}
