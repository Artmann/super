using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Paint's filter: paint_filter.comp runs an anisotropic Kuwahara filter over the picture into the half-size painted
/// picture. It stays on Vulkan until compute moves to the GPU layer.
/// </summary>
internal sealed unsafe class PaintFilter : IDisposable
{
    private const uint GroupSize = 16;

    private readonly VulkanContext _context;
    private readonly (Sampler Nearest, Sampler Linear) _samplers;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly (PipelineLayout Layout, LivePipeline Pipeline) _pipeline;

    public PaintFilter(
        VulkanContext context, (Sampler Nearest, Sampler Linear) samplers, DescriptorSetLayout frameSetLayout, DescriptorSetLayout exposureLayout)
    {
        _context = context;
        _samplers = samplers;
        _setLayout = context.CreateSetLayout(
        [
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(2, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
        ]);
        _pool = context.CreateDescriptorPool(
            1, new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 2), new DescriptorPoolSize(DescriptorType.StorageImage, 1));
        _set = context.AllocateSet(_pool, _setLayout);

        var layout = context.CreatePipelineLayout(
            [frameSetLayout, _setLayout, exposureLayout], new PushConstantRange(ShaderStageFlags.ComputeBit, 0, PaintConstants.Size));
        _pipeline = (layout, PipelineFactory.CreateCompute(context, "paint_filter.comp", layout));
    }

    /// <summary>Points the filter at <paramref name="source"/> and <paramref name="depth"/>, writing into <paramref name="painted"/>.</summary>
    public void Write(ImageView source, ImageView depth, ImageView painted)
    {
        var (nearest, linear) = _samplers;
        var sourceInfo = new DescriptorImageInfo(linear, source, ImageLayout.ShaderReadOnlyOptimal);
        var depthInfo = new DescriptorImageInfo(nearest, depth, ImageLayout.DepthReadOnlyOptimal);
        var output = new DescriptorImageInfo(default, painted, ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[]
        {
            Descriptors.ImageWrite(_set, 0, DescriptorType.CombinedImageSampler, &sourceInfo),
            Descriptors.ImageWrite(_set, 1, DescriptorType.CombinedImageSampler, &depthInfo),
            Descriptors.ImageWrite(_set, 2, DescriptorType.StorageImage, &output),
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 3, writes, 0, null);
    }

    /// <summary>Filters the picture into <paramref name="painted"/>, whose image is its half size, and leaves it ready to sample.</summary>
    public void Record(
        CommandBuffer cmd, (DescriptorSet Frame, DescriptorSet Exposure) tables, (Image Image, Extent2D Half) painted, in PaintConstants constants)
    {
        var api = _context.Api;

        // The filter reads depth from compute, which no earlier pass may have.
        api.MemoryBarrier(cmd,
            (PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentWriteBit),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit));

        // Last frame's composite read it; its contents are all replaced.
        api.Transition(cmd, new ImageTransition(painted.Image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.General),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.None),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit));

        var sets = stackalloc DescriptorSet[] { tables.Frame, _set, tables.Exposure };
        var lens = constants;
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline.Pipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipeline.Layout, 0, 3, sets, 0, null);
        api.CmdPushConstants(cmd, _pipeline.Layout, ShaderStageFlags.ComputeBit, 0, PaintConstants.Size, &lens);
        api.CmdDispatch(cmd, (painted.Half.Width + GroupSize - 1) / GroupSize, (painted.Half.Height + GroupSize - 1) / GroupSize, 1);

        api.Transition(cmd, new ImageTransition(painted.Image, ImageAspectFlags.ColorBit, ImageLayout.General, ImageLayout.ShaderReadOnlyOptimal),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));
    }

    public void Dispose()
    {
        _pipeline.Pipeline.Destroy();
        _context.Api.DestroyPipelineLayout(_context.Device, _pipeline.Layout, null);
        _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        _context.Api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
    }
}
