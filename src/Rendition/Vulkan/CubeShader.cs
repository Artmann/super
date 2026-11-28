using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// A compute shader that writes one level of an <see cref="EnvironmentCube"/>, eight by eight texels a group and one
/// face per z: binding 0 is what it reads, binding 1 the level it writes, and bindings 2 to 4 a table, a cube and a
/// volume it may also read, and <typeparamref name="TConstants"/> its push constants.
/// </summary>
internal sealed unsafe class CubeShader<TConstants> : IDisposable where TConstants : unmanaged
{
    private readonly VulkanContext _context;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly PipelineLayout _pipelineLayout;
    private readonly LivePipeline _pipeline;

    /// <param name="input">What binding 0 holds: a uniform buffer or a sampled cube.</param>
    /// <param name="sets">How many descriptor sets <see cref="CreateSet(GpuBuffer, ImageView, Sampler, ValueTuple{ImageView, ImageView}, ValueTuple{Sampler, ImageView})"/> and its overload may make.</param>
    public CubeShader(VulkanContext context, string shader, DescriptorType input, uint sets)
    {
        _context = context;
        _setLayout = context.CreateSetLayout(
        [
            new(0, input, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(2, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(3, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(4, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
        ]);
        _pool = context.CreateDescriptorPool(sets,
            new DescriptorPoolSize(input, sets),
            new DescriptorPoolSize(DescriptorType.StorageImage, sets),
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, sets * 3));
        _pipelineLayout = context.CreatePipelineLayout(
            [_setLayout],
            new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(TConstants)));
        _pipeline = PipelineFactory.CreateCompute(context, shader, _pipelineLayout);
    }

    /// <summary>
    /// A set that reads the whole of a uniform buffer, a 2D table resting in General at binding 2 and a cube at binding 3 from
    /// <paramref name="reads"/>, and a <paramref name="volume"/> at binding 4, and writes <paramref name="target"/>.
    /// </summary>
    public DescriptorSet CreateSet(GpuBuffer uniforms, ImageView target, Sampler sampler, (ImageView Table, ImageView Cube) reads,
        (Sampler Sampler, ImageView View) volume)
    {
        var set = _context.AllocateSet(_pool, _setLayout);
        var bufferInfo = new DescriptorBufferInfo(uniforms.Handle, 0, uniforms.Size);
        var imageInfo = new DescriptorImageInfo(default, target, ImageLayout.General);
        var tableInfo = new DescriptorImageInfo(sampler, reads.Table, ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[]
        {
            Descriptors.BufferWrite(set, 0, &bufferInfo, DescriptorType.UniformBuffer),
            Descriptors.ImageWrite(set, 1, DescriptorType.StorageImage, &imageInfo),
            Descriptors.ImageWrite(set, 2, DescriptorType.CombinedImageSampler, &tableInfo),
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 3, writes, 0, null);
        SetSampled(set, 3, sampler, reads.Cube, ImageLayout.ShaderReadOnlyOptimal);
        SetSampled(set, 4, volume.Sampler, volume.View, ImageLayout.General);
        return set;
    }

    /// <summary>
    /// Points binding 3, the cube, or binding 4, the volume, of <paramref name="set"/> at <paramref name="image"/>, in
    /// <paramref name="layout"/>. Nothing may be using the set.
    /// </summary>
    public void SetSampled(DescriptorSet set, uint binding, Sampler sampler, ImageView image, ImageLayout layout)
    {
        var info = new DescriptorImageInfo(sampler, image, layout);
        var write = Descriptors.ImageWrite(set, binding, DescriptorType.CombinedImageSampler, &info);
        _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    /// <summary>A set that samples <paramref name="source"/> and writes <paramref name="target"/>.</summary>
    public DescriptorSet CreateSet(Sampler sampler, ImageView source, ImageView target)
    {
        var set = _context.AllocateSet(_pool, _setLayout);
        var sourceInfo = new DescriptorImageInfo(sampler, source, ImageLayout.ShaderReadOnlyOptimal);
        var targetInfo = new DescriptorImageInfo(default, target, ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[]
        {
            Descriptors.ImageWrite(set, 0, DescriptorType.CombinedImageSampler, &sourceInfo),
            Descriptors.ImageWrite(set, 1, DescriptorType.StorageImage, &targetInfo),
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
        return set;
    }

    /// <summary>Writes the whole of one level of <paramref name="target"/>, leaving it in General.</summary>
    public void Record(CommandBuffer cmd, DescriptorSet set, CubeLevel target, TConstants constants)
    {
        var api = _context.Api;
        api.Transition(cmd,
            new ImageTransition(target.Image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.General) { BaseMip = (uint)target.Mip, LayerCount = 6 },
            (PipelineStageFlags2.AllCommandsBit, AccessFlags2.None),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit));

        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 1, &set, 0, null);
        api.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(TConstants), &constants);
        uint groups = (target.Size + 7) / 8;
        api.CmdDispatch(cmd, groups, groups, 6);
    }

    public void Dispose()
    {
        _pipeline.Destroy();
        _context.Api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        _context.Api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
    }
}
