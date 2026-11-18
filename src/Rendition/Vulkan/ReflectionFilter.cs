using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Blurs a reflection capture into the levels of a probe's cube, rougher with each level, with
/// Shaders/reflection_probe.comp. Each level of each frame in flight has its own descriptor set, written as that frame
/// records, when its last use is done.
/// </summary>
internal sealed unsafe class ReflectionFilter : IDisposable
{
    /// <summary>Must match the Filter block in Shaders/reflection_probe.comp.</summary>
    private struct FilterConstants
    {
        public float Roughness;
    }

    private readonly VulkanContext _context;
    private readonly Sampler _sampler;
    private readonly int _levels;
    private readonly uint _faceSize;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet[] _sets;
    private readonly PipelineLayout _pipelineLayout;
    private readonly LivePipeline _pipeline;

    /// <summary>A filter into cubes of <paramref name="levels"/> mips whose faces are <paramref name="faceSize"/> texels across.</summary>
    public ReflectionFilter(VulkanContext context, Sampler sampler, int framesInFlight, int levels, uint faceSize)
    {
        _context = context;
        _sampler = sampler;
        _levels = levels;
        _faceSize = faceSize;
        _setLayout = context.CreateSetLayout(
        [
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
        ]);
        uint sets = (uint)(framesInFlight * levels);
        _pool = context.CreateDescriptorPool(
            sets,
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, sets),
            new DescriptorPoolSize(DescriptorType.StorageImage, sets));
        _sets = new DescriptorSet[sets];
        context.AllocateSets(_pool, _setLayout, _sets);

        _pipelineLayout = context.CreatePipelineLayout(
            [_setLayout],
            new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FilterConstants)));
        _pipeline = PipelineFactory.CreateCompute(context, "reflection_probe.comp", _pipelineLayout);
    }

    /// <summary>
    /// Records the blur of <paramref name="capture"/>, with its mips ready to sample, into each of
    /// <paramref name="levels"/>, a view of one mip of a cube each, which must be in the general layout.
    /// </summary>
    public void Record(CommandBuffer cmd, int frameIndex, ImageView capture, ReadOnlySpan<ImageView> levels)
    {
        var api = _context.Api;
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
        for (int mip = 0; mip < _levels; mip++)
        {
            var set = _sets[frameIndex * _levels + mip];
            WriteSet(set, capture, levels[mip]);
            var constants = new FilterConstants { Roughness = mip / (float)(_levels - 1) };
            api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
            api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FilterConstants), &constants);
            uint groups = ((_faceSize >> mip) + 7) / 8;
            api.CmdDispatch(cmd, groups, groups, 6);
        }
    }

    private void WriteSet(DescriptorSet set, ImageView capture, ImageView target)
    {
        var sourceInfo = new DescriptorImageInfo(_sampler, capture, ImageLayout.ShaderReadOnlyOptimal);
        var targetInfo = new DescriptorImageInfo(default, target, ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[]
        {
            Descriptors.ImageWrite(set, 0, DescriptorType.CombinedImageSampler, &sourceInfo),
            Descriptors.ImageWrite(set, 1, DescriptorType.StorageImage, &targetInfo),
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
    }

    public void Dispose()
    {
        _pipeline.Destroy();
        _context.DestroyLayouts(_pipelineLayout, _pool, _setLayout);
    }
}
