using System.Numerics;
using Rendition.Vulkan;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Rendition.Passes;

/// <summary>
/// Projects the sky cube the light probes read past the scene onto L1 spherical harmonics, per colour, into a 64-byte
/// buffer that chunks.glsl reads at set 0, binding 12: four vec4s, coefficient k's red, green and blue and its
/// luminance, unscaled by the environment's intensity. sky_sh.comp reads the cube's 16² mip, each texel weighted by its
/// solid angle. Recorded only on frames that draw a chunk's bake. See "Chunk transfer" in docs/design.md.
/// </summary>
internal sealed unsafe class SkySh : IDisposable
{
    /// <summary>The buffer's size: four coefficients of four floats.</summary>
    public const int Size = 4 * 16;

    private readonly VulkanContext _context;
    private readonly GpuBuffer _buffer;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _pipeline;

    /// <param name="sky">The sky cube, which keeps its view for the renderer's life.</param>
    public SkySh(VulkanContext context, ImageView sky, Sampler sampler)
    {
        _context = context;
        var api = context.Api;
        _buffer = GpuBuffer.CreateHostVisible(context, Size, BufferUsageFlags.StorageBufferBit);
        new Span<byte>(_buffer.Mapped, Size).Clear();

        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.ComputeBit),
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &layoutInfo, null, out _setLayout).Check();

        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.CombinedImageSampler, 1),
            new(DescriptorType.StorageBuffer, 1),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        api.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool).Check();

        var setLayout = _setLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        api.AllocateDescriptorSets(context.Device, &allocInfo, out _set).Check();

        var imageInfo = new DescriptorImageInfo(sampler, sky, ImageLayout.ShaderReadOnlyOptimal);
        var bufferInfo = new DescriptorBufferInfo(_buffer.Handle, 0, Size);
        var writes = stackalloc WriteDescriptorSet[]
        {
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &imageInfo,
            },
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageBuffer,
                PBufferInfo = &bufferInfo,
            },
        };
        api.UpdateDescriptorSets(context.Device, 2, writes, 0, null);

        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
        };
        api.CreatePipelineLayout(context.Device, &pipelineLayoutInfo, null, out _pipelineLayout).Check();
        _pipeline = PipelineFactory.CreateCompute(context, "sky_sh.comp", _pipelineLayout);
    }

    /// <summary>The projected sky, for the frame's set 0, binding 12.</summary>
    public Buffer Handle => _buffer.Handle;

    /// <summary>
    /// Projects the sky. The scene's earlier frames may still be reading the buffer, and the G-buffer pass reads it
    /// after, so it is fenced on both sides.
    /// </summary>
    public void Record(CommandBuffer cmd)
    {
        var api = _context.Api;
        Barrier(cmd,
            PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderStorageReadBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit);
        var set = _set;
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        api.CmdDispatch(cmd, 1, 1, 1);
        Barrier(cmd,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderStorageReadBit);
    }

    /// <summary>Projects the sky now and waits, then reads the coefficients back. Stalls the GPU, so for tests.</summary>
    public Vector4[] Project()
    {
        _context.SubmitAndWait(cmd =>
        {
            Record(cmd);
            Barrier(cmd, PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.HostBit, AccessFlags2.HostReadBit);
        });
        return new ReadOnlySpan<Vector4>(_buffer.Mapped, 4).ToArray();
    }

    public void Dispose()
    {
        var api = _context.Api;
        api.DestroyPipeline(_context.Device, _pipeline, null);
        api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
        _buffer.Dispose();
    }

    private void Barrier(
        CommandBuffer cmd, PipelineStageFlags2 sourceStage, AccessFlags2 sourceAccess, PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = sourceStage,
            SrcAccessMask = sourceAccess,
            DstStageMask = destinationStage,
            DstAccessMask = destinationAccess,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }
}
