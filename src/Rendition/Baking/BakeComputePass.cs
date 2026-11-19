using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>A bake compute shader's push constants that carry the number of the pass they run.</summary>
internal interface IBakePassConstants
{
    uint Pass { set; }
}

/// <summary>
/// One of the bake's ray-traced compute shaders: its set layout, its pipeline layout with the renderer's resource table
/// as set 1, and its pipeline, and how its passes are recorded.
/// </summary>
internal sealed unsafe class BakeComputePass : IDisposable
{
    private readonly VulkanContext _context;
    private readonly SceneStores _stores;
    private readonly LivePipeline _pipeline;

    public BakeComputePass(
        VulkanContext context, SceneStores stores, ReadOnlySpan<DescriptorSetLayoutBinding> bindings, uint pushSize, string shader)
    {
        _context = context;
        _stores = stores;
        SetLayout = context.CreateSetLayout(bindings);
        Layout = context.CreatePipelineLayout(
            [SetLayout, stores.Resources.Layout], new PushConstantRange(ShaderStageFlags.ComputeBit, 0, pushSize));
        _pipeline = PipelineFactory.CreateCompute(context, shader, Layout);
    }

    public DescriptorSetLayout SetLayout { get; }

    public PipelineLayout Layout { get; }

    /// <summary>A set of the shader's own from <paramref name="pool"/>.</summary>
    public DescriptorSet AllocateSet(DescriptorPool pool) => _context.AllocateSet(pool, SetLayout);

    /// <summary>Binds the pipeline for the dispatches that follow.</summary>
    public void BindPipeline(CommandBuffer cmd) => _context.Api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _pipeline);

    /// <summary>Binds <paramref name="set"/> as set 0 and the renderer's resource table as set 1.</summary>
    public void BindSets(CommandBuffer cmd, DescriptorSet set)
    {
        var sets = stackalloc DescriptorSet[] { set, _stores.Resources.Set };
        _context.Api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, Layout, 0, 2, sets, 0, null);
    }

    /// <summary>
    /// Records the <paramref name="passes"/>, each a dispatch of <paramref name="groups"/> work groups over
    /// <paramref name="set"/> with its own pass number in the constants, each waiting for the last's writes.
    /// </summary>
    public void RecordPasses<T>(CommandBuffer cmd, DescriptorSet set, T constants, (int First, int Count) passes, (uint X, uint Y) groups)
        where T : unmanaged, IBakePassConstants
    {
        var api = _context.Api;
        BindPipeline(cmd);
        BindSets(cmd, set);
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.ComputeShaderBit,
            SrcAccessMask = AccessFlags2.ShaderStorageWriteBit,
            DstStageMask = PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit,
            DstAccessMask = AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.TransferReadBit,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        for (int pass = passes.First; pass < passes.First + passes.Count; pass++)
        {
            constants.Pass = (uint)pass;
            api.CmdPushConstants(cmd, Layout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(T), &constants);
            api.CmdDispatch(cmd, groups.X, groups.Y, 1);
            api.CmdPipelineBarrier2(cmd, &dependency);
        }
    }

    public void Dispose()
    {
        _pipeline.Destroy();
        var api = _context.Api;
        api.DestroyPipelineLayout(_context.Device, Layout, null);
        api.DestroyDescriptorSetLayout(_context.Device, SetLayout, null);
    }
}
