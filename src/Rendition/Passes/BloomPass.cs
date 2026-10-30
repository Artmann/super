using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

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
    private readonly RenderTarget[] _down = new RenderTarget[Levels];
    private readonly RenderTarget[] _up = new RenderTarget[Levels - 1];
    private readonly Extent2D[] _extents = new Extent2D[Levels];

    public ImageView Result => _up[0].View;

    public BloomPass(VulkanContext context, Sampler sampler, ImageView hdr, Extent2D extent)
    {
        _context = context;
        _sampler = sampler;
        var api = context.Api;

        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &layoutInfo, null, out _setLayout).Check();

        int setCount = Levels + Levels - 1;
        var poolSize = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, (uint)(setCount * 2));
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = (uint)setCount,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        api.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool).Check();

        for (int i = 0; i < setCount; i++)
        {
            var setLayout = _setLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _pool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            api.AllocateDescriptorSets(context.Device, &allocInfo, out var set).Check();
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

        CreateTargets(hdr, extent);
    }

    /// <summary>Remakes the targets for a new window size.</summary>
    public void Resize(ImageView hdr, Extent2D extent)
    {
        DestroyTargets();
        CreateTargets(hdr, extent);
    }

    /// <summary>Expects the HDR image to be ready for sampling.</summary>
    public void Record(CommandBuffer cmd)
    {
        var api = _context.Api;

        for (int i = 0; i < Levels; i++)
        {
            api.BeginColorTarget(cmd, _down[i].Image);
            _downPass.Record(cmd, _down[i].View, _extents[i], _downSets.AsSpan(i, 1));
            api.EndColorTarget(cmd, _down[i].Image);
        }

        for (int i = Levels - 2; i >= 0; i--)
        {
            api.BeginColorTarget(cmd, _up[i].Image);
            _upPass.Record(cmd, _up[i].View, _extents[i], _upSets.AsSpan(i, 1));
            api.EndColorTarget(cmd, _up[i].Image);
        }
    }

    public void Dispose()
    {
        DestroyTargets();
        _upPass.Dispose();
        _downPass.Dispose();
        _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        _context.Api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
    }

    private void CreateTargets(ImageView hdr, Extent2D extent)
    {
        for (int i = 0; i < Levels; i++)
        {
            _extents[i] = new Extent2D(Math.Max(extent.Width >> (i + 1), 1), Math.Max(extent.Height >> (i + 1), 1));
            _down[i] = CreateTarget(_extents[i]);
            if (i < Levels - 1)
            {
                _up[i] = CreateTarget(_extents[i]);
            }
        }

        for (int i = 0; i < Levels; i++)
        {
            var source = i == 0 ? hdr : _down[i - 1].View;
            WriteSet(_downSets[i], source, source);
        }

        for (int i = 0; i < Levels - 1; i++)
        {
            WriteSet(_upSets[i], _down[i].View, i == Levels - 2 ? _down[i + 1].View : _up[i + 1].View);
        }
    }

    private RenderTarget CreateTarget(Extent2D extent) =>
        new(_context, GBuffer.HdrFormat, extent, ImageUsageFlags.ColorAttachmentBit, ImageAspectFlags.ColorBit);

    private void DestroyTargets()
    {
        foreach (var target in _down)
        {
            target.Dispose();
        }

        foreach (var target in _up)
        {
            target.Dispose();
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
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = (uint)i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &images[i],
            };
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
    }
}
