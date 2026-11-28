using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
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
internal sealed class BloomPass : IDisposable
{
    private const int Levels = 5;

    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly FullscreenPass _downPass;
    private readonly FullscreenPass _upPass;
    private readonly CommandList _commands = new();
    private readonly ImageGroup[] _downGroups = new ImageGroup[Levels];
    private readonly ImageGroup[] _upGroups = new ImageGroup[Levels - 1];
    private readonly AdoptedTarget[] _downTargets = new AdoptedTarget[Levels];
    private readonly AdoptedTarget[] _upTargets = new AdoptedTarget[Levels - 1];
    private BloomLevels _levels;

    public ImageView Result => _levels.Up[0].View;

    public BloomPass((VulkanContext Context, VulkanGpu Gpu) device, ImageView hdr, Extent2D extent)
    {
        var (context, gpu) = device;
        _context = context;
        _gpu = gpu;
        var sampler = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear));
        for (int i = 0; i < Levels; i++)
        {
            _downGroups[i] = new ImageGroup(gpu, sampler);
            _downTargets[i] = new AdoptedTarget(gpu);
            if (i < Levels - 1)
            {
                _upGroups[i] = new ImageGroup(gpu, sampler);
                _upTargets[i] = new AdoptedTarget(gpu);
            }
        }

        _downPass = new FullscreenPass(gpu, "bloom_down.frag", [GpuFormat.Rgba16Float]);
        _upPass = new FullscreenPass(gpu, "bloom_up.frag", [GpuFormat.Rgba16Float]);

        _levels = new BloomLevels(context, extent, Levels);
        MakeGroups(hdr);
    }

    /// <summary>Remakes the targets for a new window size. Frames still in flight must have finished.</summary>
    public void Resize(ImageView hdr, Extent2D extent)
    {
        _levels.Dispose();
        _levels = new BloomLevels(_context, extent, Levels);
        MakeGroups(hdr);
    }

    /// <summary>Reads a different HDR image from the next frame on. Frames still in flight must have finished.</summary>
    public void SetSource(ImageView hdr) => _downGroups[0].Make(_downPass.Pipeline, 0, [("sourceTexture", hdr)]);

    /// <summary>
    /// Records bloom, or leaves it out of this frame when <paramref name="off"/> holds <see cref="RenderEffects.Bloom"/>.
    /// Left out, nothing reads <see cref="Result"/>, but tonemapping still binds it, so it's made ready to sample.
    /// Expects the HDR image to be ready for sampling.
    /// </summary>
    public void Record(CommandBuffer cmd, RenderEffects off)
    {
        if (!off.HasFlag(RenderEffects.Bloom))
        {
            RecordLevels(cmd);
            return;
        }

        _context.Api.Transition(cmd, new ImageTransition(_levels.Up[0].Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));
    }

    private void RecordLevels(CommandBuffer cmd)
    {
        var (down, up, extents) = (_levels.Down, _levels.Up, _levels.Extents);
        _commands.Reset();
        for (int i = 0; i < Levels; i++)
        {
            _downPass.Record(_commands, [new ColorTarget(_downTargets[i].For(down[i], extents[i]), LoadOp.DontCare, default)],
                [_downGroups[i].Id], []);
        }

        for (int i = Levels - 2; i >= 0; i--)
        {
            _upPass.Record(_commands, [new ColorTarget(_upTargets[i].For(up[i], extents[i]), LoadOp.DontCare, default)],
                [_upGroups[i].Id], []);
        }

        _gpu.Play(_commands, cmd);
    }

    public void Dispose()
    {
        foreach (var part in _downGroups.Concat<IDisposable>(_upGroups).Concat(_downTargets).Concat(_upTargets))
        {
            part.Dispose();
        }

        _levels.Dispose();
        _upPass.Dispose();
        _downPass.Dispose();
    }

    // Points each step down at the one before it, the first at the HDR image, and each step up at its own level and
    // the one below.
    private void MakeGroups(ImageView hdr)
    {
        var (down, up) = (_levels.Down, _levels.Up);
        for (int i = 0; i < Levels; i++)
        {
            _downGroups[i].Make(_downPass.Pipeline, 0, [("sourceTexture", i == 0 ? hdr : down[i - 1].View)]);
        }

        for (int i = 0; i < Levels - 1; i++)
        {
            var wide = i == Levels - 2 ? down[i + 1].View : up[i + 1].View;
            _upGroups[i].Make(_upPass.Pipeline, 0, [("sharpTexture", down[i].View), ("wideTexture", wide)]);
        }
    }
}
