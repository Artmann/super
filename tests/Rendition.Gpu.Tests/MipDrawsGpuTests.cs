using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Tests;

[Collection(GpuCollection.Name)]
[Trait("Needs", "GPU")]
public sealed class MipDrawsGpuTests(GpuFixture gpu)
{
    private const uint Layers = 2;
    private const uint Mips = 3;
    private static readonly Extent2D Square = new(4, 4);

    // Mip 0 of the first layer, by its red channel: four 2x2 blocks averaging 12, 44, 104 and 200, which average 90.
    private static readonly byte[] Reds =
    [
        0, 8, 40, 44,
        16, 24, 44, 48,
        100, 104, 196, 200,
        104, 108, 200, 204,
    ];

    [Fact]
    public void Each_mip_is_the_average_of_the_four_texels_above_it()
    {
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var target = Target(Square);
        Upload(target, Square, Layer(0), 0, 0);
        Upload(target, Square, Layer(1), 0, 1);
        using var chain = new MipDraws(device, gpu.Context, new MipImage(target.Image, GpuFormat.Rgba8Unorm, Square, Mips, Layers));

        Play(device, chain.Record);

        // The second layer holds 255 minus the first in red and the first in green, so its averages follow.
        Assert.Equal(Pixels((12, 0), (44, 0), (104, 0), (200, 0)), Read(target, new Extent2D(2, 2), 1, 0));
        Assert.Equal(Pixels((243, 12), (211, 44), (151, 104), (55, 200)), Read(target, new Extent2D(2, 2), 1, 1));
        Assert.Equal(Pixels((90, 0)), Read(target, new Extent2D(1, 1), 2, 0));
        Assert.Equal(Pixels((165, 90)), Read(target, new Extent2D(1, 1), 2, 1));
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_chain_of_odd_sizes_matches_a_linear_blit()
    {
        int errors = gpu.ValidationErrors;
        var extent = new Extent2D(5, 3);
        using var device = new VulkanGpu(gpu.Context);
        using var drawn = Target(extent, layers: 1, mips: 2);
        using var blitted = Target(extent, layers: 1, mips: 2);
        var pattern = Pattern(extent);
        Upload(drawn, extent, pattern, 0, 0);
        Upload(blitted, extent, pattern, 0, 0);
        using var chain = new MipDraws(device, gpu.Context, new MipImage(drawn.Image, GpuFormat.Rgba8Unorm, extent, 2, 1));

        Play(device, chain.Record);
        Blit(blitted.Image, extent, new Extent2D(2, 1));

        var expected = Read(blitted, new Extent2D(2, 1), 1, 0);
        var actual = Read(drawn, new Extent2D(2, 1), 1, 0);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.InRange(actual[i], expected[i] - 1, expected[i] + 1);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public unsafe void A_view_drawn_to_moves_only_its_own_mip_and_layer()
    {
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var target = Target(Square);
        var half = new Extent2D(2, 2);
        Upload(target, Square, Layer(0), 0, 0);
        Upload(target, Square, Layer(1), 0, 1);
        Upload(target, half, Pixels((1, 2), (3, 4), (5, 6), (7, 8)), 1, 0);
        var view = CreateView(target.Image, 1, 1);
        using var pass = new FullscreenPass(device, "solid_color.frag", [GpuFormat.Rgba8Unorm]);
        var color = new Vector4(0f, 1f, 0f, 1f);
        var commands = new CommandList();
        var adopted = device.Adopt(new AdoptedView(view, target.Image, half) { Mip = 1, Layer = 1 });
        pass.Record(commands, [new ColorTarget(adopted, LoadOp.DontCare, default)], [], MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(in color)));

        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));
        gpu.Context.Api.DestroyImageView(gpu.Context.Device, view, null);

        Assert.Equal(Pixels((0, 255), (0, 255), (0, 255), (0, 255)), Read(target, half, 1, 1));
        Assert.Equal(Pixels((1, 2), (3, 4), (5, 6), (7, 8)), Read(target, half, 1, 0));
        Assert.Equal(Layer(0), Read(target, Square, 0, 0));
        Assert.Equal(Layer(1), Read(target, Square, 0, 1));
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Copying_into_mip_0_keeps_every_texel()
    {
        int errors = gpu.ValidationErrors;
        var extent = new Extent2D(5, 3);
        using var device = new VulkanGpu(gpu.Context);
        using var source = Target(extent, layers: 1, mips: 1);
        using var target = Target(extent, layers: 1, mips: 2);
        Upload(source, extent, Pattern(extent), 0, 0);
        using var chain = new MipDraws(
            device, gpu.Context, new MipImage(target.Image, GpuFormat.Rgba8Unorm, extent, 2, 1), copyFrom: source.View);

        Play(device, chain.Record);

        Assert.Equal(Pattern(extent), Read(target, extent, 0, 0));
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    // The first layer holds the reds above; the second 255 minus them, with the reds in green.
    private static byte[] Layer(int layer)
    {
        var pixels = new byte[Reds.Length * 4];
        for (int i = 0; i < Reds.Length; i++)
        {
            pixels[i * 4] = layer == 0 ? Reds[i] : (byte)(255 - Reds[i]);
            pixels[i * 4 + 1] = layer == 0 ? (byte)0 : Reds[i];
            pixels[i * 4 + 3] = 255;
        }

        return pixels;
    }

    private static byte[] Pixels(params (byte Red, byte Green)[] pixels) =>
        pixels.SelectMany(pixel => new[] { pixel.Red, pixel.Green, (byte)0, (byte)255 }).ToArray();

    private static byte[] Pattern(Extent2D extent)
    {
        int count = (int)(extent.Width * extent.Height);
        var pixels = new byte[count * 4];
        for (int i = 0; i < count; i++)
        {
            pixels[i * 4] = (byte)(i * 17);
            pixels[i * 4 + 1] = (byte)(250 - i * 13);
            pixels[i * 4 + 2] = (byte)(i * 61 % 256);
            pixels[i * 4 + 3] = 255;
        }

        return pixels;
    }

    private void Play(VulkanGpu device, Action<CommandList> record)
    {
        var commands = new CommandList();
        record(commands);
        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));
    }

    // What CmdBlitImage with a linear filter makes of mip 0, for comparison: left resting ready to sample.
    private unsafe void Blit(Image image, Extent2D from, Extent2D to)
    {
        var api = gpu.Context.Api;
        gpu.Context.SubmitAndWait(cmd =>
        {
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal),
                (PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit), (PipelineStageFlags2.BlitBit, AccessFlags2.TransferReadBit));
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.TransferDstOptimal) { BaseMip = 1 },
                (PipelineStageFlags2.None, AccessFlags2.None), (PipelineStageFlags2.BlitBit, AccessFlags2.TransferWriteBit));
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 1, 0, 1),
            };
            blit.SrcOffsets[1] = new Offset3D((int)from.Width, (int)from.Height, 1);
            blit.DstOffsets[1] = new Offset3D((int)to.Width, (int)to.Height, 1);
            api.CmdBlitImage(cmd, image, ImageLayout.TransferSrcOptimal, image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal),
                (PipelineStageFlags2.BlitBit, AccessFlags2.TransferReadBit), (PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit));
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal) { BaseMip = 1 },
                (PipelineStageFlags2.BlitBit, AccessFlags2.TransferWriteBit), (PipelineStageFlags2.AllCommandsBit, AccessFlags2.ShaderSampledReadBit));
        });
    }

    private unsafe ImageView CreateView(Image image, uint mip, uint layer)
    {
        var info = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, mip, 1, layer, 1),
        };
        gpu.Context.Api.CreateImageView(gpu.Context.Device, &info, null, out var view).Check();
        return view;
    }

    private RenderTarget Target(Extent2D extent, uint layers = Layers, uint mips = Mips) => new(
        gpu.Context, Format.R8G8B8A8Unorm, extent,
        ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit,
        layers: layers, mips: mips);

    private void Upload(RenderTarget target, Extent2D extent, byte[] pixels, uint mip, uint layer) =>
        LayerImages.Upload(gpu.Context, target.Image, extent, pixels, mip, layer);

    private byte[] Read(RenderTarget target, Extent2D extent, uint mip, uint layer) =>
        LayerImages.Read(gpu.Context, target.Image, extent, mip, layer);
}
