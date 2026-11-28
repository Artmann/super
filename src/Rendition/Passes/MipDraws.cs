using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>An image whose mips <see cref="MipDraws"/> fills: its format, the size of its first mip, and how many mips and layers it has.</summary>
internal readonly record struct MipImage(Image Image, GpuFormat Format, Extent2D Extent, uint Mips, uint Layers);

/// <summary>
/// Fills an image's mips by drawing each from the one above through the GPU layer, which WebGPU can do too, as it has
/// no blits. Glass looks through such a copy of the scene, the planar reflection blurs its mirror picture with one, and
/// the sky's and the reflection probes' cubes are blurred from theirs.
/// </summary>
internal sealed unsafe class MipDraws : IDisposable
{
    private readonly VulkanContext _context;
    private readonly VulkanGpu _gpu;
    private readonly FullscreenPass _pass;
    private readonly uint _mips;
    private readonly CommandList _commands = new();

    // One 2D view of each mip of each layer, a layer's mips together, adopted to be drawn to.
    private readonly ImageView[] _views;
    private readonly ViewId[] _targets;

    // The group that samples each view, for drawing the mip below it. The last mip of a layer has none.
    private readonly BindGroupId[] _sources;

    // The picture copied into the first mip before halving, when there is one.
    private readonly ViewId _copySource;
    private readonly BindGroupId _copy;

    /// <summary>
    /// Makes the views of <paramref name="image"/>'s mips to draw them. With <paramref name="copyFrom"/>, a view of a
    /// picture the size of the first mip, <see cref="Record"/> copies that into the first mip of the first layer first.
    /// </summary>
    public MipDraws(VulkanGpu gpu, VulkanContext context, MipImage image, ImageView copyFrom = default)
    {
        _context = context;
        _gpu = gpu;
        _mips = image.Mips;
        _pass = new FullscreenPass(gpu, "mip_down.frag", [image.Format]);
        int count = (int)(image.Mips * image.Layers);
        _views = new ImageView[count];
        _targets = new ViewId[count];
        _sources = new BindGroupId[count];
        var linear = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Linear));
        for (int i = 0; i < count; i++)
        {
            uint layer = (uint)i / image.Mips;
            uint mip = (uint)i % image.Mips;
            _views[i] = CreateView(image, mip, layer);
            var extent = new Extent2D(Math.Max(1, image.Extent.Width >> (int)mip), Math.Max(1, image.Extent.Height >> (int)mip));
            _targets[i] = gpu.Adopt(new AdoptedView(_views[i], image.Image, extent) { Mip = mip, Layer = layer });
            if (mip + 1 < image.Mips)
            {
                _sources[i] = gpu.CreateBindGroup(_pass.Pipeline, 0, [Bind.Image("sourceTexture", _targets[i], linear)]);
            }
        }

        if (copyFrom.Handle != 0)
        {
            // Nearest, so each pixel takes exactly the texel under it.
            _copySource = gpu.Adopt(copyFrom);
            var nearest = gpu.CreateSampler(new SamplerDescription(SamplerFilter.Nearest));
            _copy = gpu.CreateBindGroup(_pass.Pipeline, 0, [Bind.Image("sourceTexture", _copySource, nearest)]);
        }
    }

    /// <summary>
    /// Records the copy, if there is one, then each mip of each layer drawn from the one above, a pass each, leaving
    /// every mip resting ready to sample. Without a copy, the first mip of every layer must be resting ready to sample.
    /// </summary>
    public void Record(CommandList commands)
    {
        if (_copy.Value != 0)
        {
            Draw(commands, 0, _copy);
        }

        for (int i = 0; i < _targets.Length; i++)
        {
            if (i % _mips != 0)
            {
                Draw(commands, i, _sources[i - 1]);
            }
        }
    }

    /// <summary>Records the draws, as <see cref="Record"/> does, and plays them into <paramref name="cmd"/>.</summary>
    public void Play(CommandBuffer cmd)
    {
        _commands.Reset();
        Record(_commands);
        _gpu.Play(_commands, cmd);
    }

    // Nothing the mip held is kept: every pixel is drawn.
    private void Draw(CommandList commands, int target, BindGroupId source) =>
        _pass.Record(commands, [new ColorTarget(_targets[target], LoadOp.DontCare, default)], [source], []);

    private ImageView CreateView(in MipImage image, uint mip, uint layer)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image.Image,
            ViewType = ImageViewType.Type2D,
            Format = VulkanFormats.Of(image.Format),
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, mip, 1, layer, 1),
        };
        _context.Api.CreateImageView(_context.Device, &info, null, out var view).Check();
        return view;
    }

    public void Dispose()
    {
        if (_copy.Value != 0)
        {
            _gpu.Destroy(_copy);
            _gpu.Release(_copySource);
        }

        for (int i = 0; i < _views.Length; i++)
        {
            if (_sources[i].Value != 0)
            {
                _gpu.Destroy(_sources[i]);
            }

            _gpu.Release(_targets[i]);
            _context.Api.DestroyImageView(_context.Device, _views[i], null);
        }

        _pass.Dispose();
    }
}
