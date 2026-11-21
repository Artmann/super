using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The clouds' three compute passes and their sets: cloud_shadow.comp draws the shadow round the camera,
/// cloud_march.comp marches a quarter of the picture's pixels through the clouds, and cloud_resolve.comp fills out the
/// rest from last frame's picture.
/// </summary>
internal sealed unsafe class CloudPasses : IDisposable
{
    private readonly VulkanContext _context;
    private readonly CloudNoise _noise;
    private readonly Sampler _linear;
    private readonly CloudSets _sets;
    private readonly PipelineLayout _layout;
    private readonly LivePipeline _shadow;
    private readonly LivePipeline _march;
    private readonly LivePipeline _resolve;
    private DescriptorImageInfo _skyView;

    public CloudPasses(VulkanContext context, DescriptorSetLayout frameSetLayout, Sampler linear, CloudNoise noise)
    {
        _context = context;
        _noise = noise;
        _linear = linear;
        _sets = new CloudSets(context);
        _layout = context.CreatePipelineLayout([frameSetLayout, _sets.Layout]);
        _shadow = PipelineFactory.CreateCompute(context, "cloud_shadow.comp", _layout);
        _march = PipelineFactory.CreateCompute(context, "cloud_march.comp", _layout);
        _resolve = PipelineFactory.CreateCompute(context, "cloud_resolve.comp", _layout);
    }

    /// <summary>
    /// Points the march at the physical sky's sky-view table, which it takes the sky's light from, and at the noise,
    /// once both are there. Call again after the noise volumes are made.
    /// </summary>
    public void SetSources(in DescriptorImageInfo skyView)
    {
        _skyView = skyView;
        if (_noise.Volumes is { } volumes && _skyView.ImageView.Handle != 0)
        {
            _sets.SetSources(_noise.Repeat, (volumes.Shape, volumes.Detail, _noise.Weather), _skyView);
        }
    }

    /// <summary>Points the sets at new targets and at the noise. Only while no frame in flight uses them.</summary>
    public void PointAt(CloudTargets targets)
    {
        SetSources(_skyView);
        _sets.PointAt(_linear, (targets.March.View, targets.Picture.View, targets.Shadow.View),
            targets.Histories[0].View, targets.Histories[1].View);
    }

    /// <summary>
    /// Draws the shadow map when <paramref name="shadowed"/>, marches the clouds and fills out their picture into
    /// history <paramref name="history"/>.
    /// </summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet, CloudTargets targets, int history, bool shadowed)
    {
        var api = _context.Api;

        // Last frame's passes read the picture, the shadow map and the history, and wrote what is history now.
        api.BeginHistoryCompute(cmd, _layout, frameSet, _sets.Sets[history]);

        // Nothing else of the clouds reads the shadow map, so the march goes straight on.
        if (shadowed)
        {
            api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _shadow);
            const uint shadowGroups = CloudModel.ShadowResolution / 8;
            api.CmdDispatch(cmd, shadowGroups, shadowGroups, 1);
        }

        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _march);
        api.CmdDispatch(cmd, Groups(targets.MarchSize.Width), Groups(targets.MarchSize.Height), 1);

        api.MemoryBarrier(cmd,
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit));

        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _resolve);
        api.CmdDispatch(cmd, Groups(targets.Size.Width), Groups(targets.Size.Height), 1);

        api.MemoryBarrier(cmd,
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));
    }

    public void Dispose()
    {
        _shadow.Destroy();
        _march.Destroy();
        _resolve.Destroy();
        _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
        _sets.Dispose();
    }

    private static uint Groups(int texels) => (uint)(texels + 7) / 8;
}
