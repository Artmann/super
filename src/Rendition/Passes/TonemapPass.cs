using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Tonemaps the picture, with bloom and the lens flare added, into Ldr. The first pass drawn through the GPU layer:
/// its own images are a bind group made by binding name, and the frame's, the meter's and the grade's sets, which
/// passes that haven't moved still make, are adopted beside it.
/// </summary>
internal sealed class TonemapPass : IDisposable
{
    private readonly VulkanGpu _gpu;
    private readonly FullscreenPass _pass;
    private readonly SamplerId _sampler;
    private readonly CommandList _commands = new();
    private readonly ViewId[] _sources = new ViewId[3];
    private BindGroupId _group;
    private (ImageView Handle, Extent2D Extent, ViewId Id) _ldr;

    public TonemapPass(VulkanGpu gpu, DescriptorKit kit, DescriptorSetLayout meterLayout)
    {
        _gpu = gpu;
        // Group 1 is the pass's own, laid out from tonemap.frag; the rest are lent by the passes that fill them.
        _pass = new FullscreenPass(gpu, "tonemap.frag", [GpuFormat.Rgba8UnormSrgb],
            borrowed: [kit.FrameSetLayout, default, meterLayout, kit.OneTextureLayout]);
        _sampler = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear));
    }

    /// <summary>
    /// Points the pass at the picture, bloom and the flare. Makes its bind group again, so it must only be called
    /// while the GPU is idle, as every caller of <see cref="PostStage.WriteSets"/> is.
    /// </summary>
    public void SetSources(ImageView source, ImageView bloom, ImageView flare)
    {
        ReleaseSources();
        _sources[0] = _gpu.Adopt(source);
        _sources[1] = _gpu.Adopt(bloom);
        _sources[2] = _gpu.Adopt(flare);
        _group = _gpu.CreateBindGroup(_pass.Pipeline, 1,
        [
            Bind.Image("hdrTexture", _sources[0], _sampler),
            Bind.Image("bloomTexture", _sources[1], _sampler),
            Bind.Image("flareTexture", _sources[2], _sampler),
        ]);
    }

    /// <summary>
    /// Tonemaps into <paramref name="ldr"/>, the target and its size, reading the frame's, the meter's and the
    /// grade's sets.
    /// </summary>
    public void Record(CommandBuffer cmd, (RenderTarget Target, Extent2D Extent) ldr, (DescriptorSet Frame, DescriptorSet Meter, DescriptorSet Grade) sets)
    {
        // Made again with the targets, maybe with the same handle but a new size. Silk's Extent2D.Equals boxes, so
        // the sides are compared one by one.
        var (view, extent) = (ldr.Target.View, ldr.Extent);
        if (_ldr.Handle.Handle != view.Handle || _ldr.Extent.Width != extent.Width || _ldr.Extent.Height != extent.Height)
        {
            ReleaseLdr();
            _ldr = (ldr.Target.View, ldr.Extent, _gpu.Adopt(ldr.Target.View, ldr.Target.Image, ldr.Extent));
        }

        _commands.Reset();
        _pass.Record(_commands, [new ColorTarget(_ldr.Id, LoadOp.DontCare, default)],
            [_gpu.Adopt(sets.Frame), _group, _gpu.Adopt(sets.Meter), _gpu.Adopt(sets.Grade)], []);
        _gpu.Play(_commands, cmd);
    }

    private void ReleaseSources()
    {
        if (_group.Value == 0)
        {
            return;
        }

        _gpu.Destroy(_group);
        foreach (var source in _sources)
        {
            _gpu.Release(source);
        }
    }

    private void ReleaseLdr()
    {
        if (_ldr.Id.Value != 0)
        {
            _gpu.Release(_ldr.Id);
        }
    }

    public void Dispose()
    {
        ReleaseSources();
        ReleaseLdr();
        _pass.Dispose();
    }
}
