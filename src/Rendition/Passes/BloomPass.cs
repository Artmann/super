using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Bloom's targets: each step down halves the size, and each step up but the last has a target the size of the step
/// down it matches.
/// </summary>
internal sealed class BloomLevels : IDisposable
{
    public BloomLevels(VulkanContext context, Extent2D extent, int levels)
    {
        Down = new RenderTarget[levels];
        Up = new RenderTarget[levels - 1];
        Extents = new Extent2D[levels];
        for (int i = 0; i < levels; i++)
        {
            Extents[i] = new Extent2D(Math.Max(extent.Width >> (i + 1), 1), Math.Max(extent.Height >> (i + 1), 1));
            Down[i] = Create(context, Extents[i]);
            if (i < levels - 1)
            {
                Up[i] = Create(context, Extents[i]);
            }
        }
    }

    public RenderTarget[] Down { get; }

    public RenderTarget[] Up { get; }

    public Extent2D[] Extents { get; }

    public void Dispose()
    {
        foreach (var target in Down)
        {
            target.Dispose();
        }

        foreach (var target in Up)
        {
            target.Dispose();
        }
    }

    private static RenderTarget Create(VulkanContext context, Extent2D extent) =>
        new(context, GBuffer.HdrFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);
}

/// <summary>
/// The soft glow around bright things. It shrinks the image step by step and adds the
/// steps back together, which gives a wide blur for little work.
/// </summary>
internal sealed unsafe class BloomPass : IDisposable
{
    private const int Levels = 5;

    private readonly VulkanContext _context;
    private readonly Sampler _sampler;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly FullscreenPass _downPass;
    private readonly FullscreenPass _upPass;
    private readonly DescriptorSet[] _downSets = new DescriptorSet[Levels];
    private readonly DescriptorSet[] _upSets = new DescriptorSet[Levels - 1];
    private BloomLevels _levels;

    public ImageView Result => _levels.Up[0].View;

    public BloomPass(VulkanContext context, Sampler sampler, ImageView hdr, Extent2D extent)
    {
        _context = context;
        _sampler = sampler;

        _setLayout = context.CreateSetLayout(
        [
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
        ]);

        int setCount = Levels + Levels - 1;
        _pool = context.CreateDescriptorPool(
            (uint)setCount, new DescriptorPoolSize(DescriptorType.CombinedImageSampler, (uint)(setCount * 2)));

        for (int i = 0; i < setCount; i++)
        {
            var set = context.AllocateSet(_pool, _setLayout);
            if (i < Levels)
            {
                _downSets[i] = set;
            }
            else
            {
                _upSets[i - Levels] = set;
            }
        }

        _downPass = new FullscreenPass(context, "bloom_down.frag", GBuffer.HdrFormat, [_setLayout]);
        _upPass = new FullscreenPass(context, "bloom_up.frag", GBuffer.HdrFormat, [_setLayout]);

        _levels = new BloomLevels(context, extent, Levels);
        WriteSets(hdr);
    }

    /// <summary>Remakes the targets for a new window size.</summary>
    public void Resize(ImageView hdr, Extent2D extent)
    {
        _levels.Dispose();
        _levels = new BloomLevels(_context, extent, Levels);
        WriteSets(hdr);
    }

    /// <summary>Reads a different HDR image from the next frame on. Frames still in flight must have finished.</summary>
    public void SetSource(ImageView hdr) => WriteSet(_downSets[0], hdr, hdr);

    /// <summary>
    /// Leaves bloom out of this frame. Nothing reads <see cref="Result"/> then, but tonemapping
    /// still binds it, so it's made ready to sample.
    /// </summary>
    public void RecordSkipped(CommandBuffer cmd) =>
        _context.Api.Transition(cmd, new ImageTransition(_levels.Up[0].Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));

    /// <summary>Expects the HDR image to be ready for sampling.</summary>
    public void Record(CommandBuffer cmd)
    {
        var api = _context.Api;
        var (down, up, extents) = (_levels.Down, _levels.Up, _levels.Extents);

        for (int i = 0; i < Levels; i++)
        {
            api.BeginColorTarget(cmd, down[i].Image);
            _downPass.Record(cmd, down[i].View, extents[i], _downSets.AsSpan(i, 1));
            api.EndColorTarget(cmd, down[i].Image);
        }

        for (int i = Levels - 2; i >= 0; i--)
        {
            api.BeginColorTarget(cmd, up[i].Image);
            _upPass.Record(cmd, up[i].View, extents[i], _upSets.AsSpan(i, 1));
            api.EndColorTarget(cmd, up[i].Image);
        }
    }

    public void Dispose()
    {
        _levels.Dispose();
        _upPass.Dispose();
        _downPass.Dispose();
        _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        _context.Api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
    }

    // Points each step down at the one before it, the first at the HDR image, and each step up at its own level and
    // the one below.
    private void WriteSets(ImageView hdr)
    {
        var (down, up) = (_levels.Down, _levels.Up);
        for (int i = 0; i < Levels; i++)
        {
            var source = i == 0 ? hdr : down[i - 1].View;
            WriteSet(_downSets[i], source, source);
        }

        for (int i = 0; i < Levels - 1; i++)
        {
            WriteSet(_upSets[i], down[i].View, i == Levels - 2 ? down[i + 1].View : up[i + 1].View);
        }
    }

    private void WriteSet(DescriptorSet set, ImageView first, ImageView second)
    {
        var images = stackalloc DescriptorImageInfo[]
        {
            new(_sampler, first, ImageLayout.ShaderReadOnlyOptimal),
            new(_sampler, second, ImageLayout.ShaderReadOnlyOptimal),
        };
        var writes = stackalloc WriteDescriptorSet[2];
        for (int i = 0; i < 2; i++)
        {
            writes[i] = Descriptors.ImageWrite(set, (uint)i, DescriptorType.CombinedImageSampler, &images[i]);
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
    }
}
