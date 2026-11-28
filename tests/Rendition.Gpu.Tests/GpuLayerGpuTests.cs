using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Gpu.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Tests;

[Collection(GpuCollection.Name)]
[Trait("Needs", "GPU")]
public sealed class GpuLayerGpuTests(GpuFixture gpu)
{
    private const int Size = 4;

    [Fact]
    public void A_fullscreen_draw_through_the_layer_fills_the_target()
    {
        byte[] pixels = DrawSolid(new Vector4(0.25f, 0.5f, 0.75f, 1f));

        for (int i = 0; i < Size * Size; i++)
        {
            var pixel = pixels.AsSpan(i * 4, 4);
            Assert.InRange(pixel[0], 63, 65);
            Assert.InRange(pixel[1], 127, 129);
            Assert.InRange(pixel[2], 190, 192);
            Assert.Equal(255, pixel[3]);
        }
    }

    [Fact]
    public void A_bind_by_a_name_the_shader_lacks_throws()
    {
        var error = BindToTonemap(1, "nope");

        Assert.Equal("Shader 'tonemap.frag' has no binding named 'nope'.", error.Message);
    }

    [Fact]
    public void A_bind_to_a_group_the_binding_is_not_in_throws()
    {
        var error = BindToTonemap(0, "hdrTexture");

        Assert.Equal("Shader 'tonemap.frag' has 'hdrTexture' in group 1, not group 0.", error.Message);
    }

    [Fact]
    public void A_fullscreen_pass_draws_the_same_through_the_layer_as_before()
    {
        using var device = new VulkanGpu(gpu.Context);
        using var kit = new DescriptorKit(gpu.Context);
        using var source = Target(Size * 2);
        Upload(source, Size * 2, Pattern(Size * 2));

        byte[] before = DownsampleTheOldWay(kit, source);
        byte[] after = DownsampleThroughTheLayer(device, source);

        Assert.True(after.Distinct().Count() > 8, "The pattern should reach the picture, not one flat colour.");
        Assert.Equal(before, after);
    }

    [Fact]
    public void Drawing_to_a_view_adopted_without_its_image_throws()
    {
        using var device = new VulkanGpu(gpu.Context);
        using var target = Target();
        var view = device.Adopt(target.View);
        using var pass = new FullscreenPass(device, "solid_color.frag", [GpuFormat.Rgba8Unorm]);
        var commands = new CommandList();
        pass.Record(commands, [new ColorTarget(view, LoadOp.DontCare, default)], [], new byte[16]);

        var error = Assert.Throws<InvalidOperationException>(() => gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd)));

        Assert.Equal($"View {view.Value} was adopted without its image, to be read, so it can't be drawn to.", error.Message);
    }

    [Fact]
    public void Adopting_a_set_twice_gives_the_same_id()
    {
        using var device = new VulkanGpu(gpu.Context);
        using var kit = new DescriptorKit(gpu.Context);
        var set = kit.NewSet(kit.OneTextureLayout);

        Assert.Equal(device.Adopt(set), device.Adopt(set));
    }

    [Fact]
    public void Recording_and_playing_a_pass_again_allocates_nothing()
    {
        using var device = new VulkanGpu(gpu.Context);
        using var kit = new DescriptorKit(gpu.Context);
        using var source = Target();
        using var target = Target();
        Upload(source, Size, Pattern(Size));
        using var pass = new FullscreenPass(device, "bloom_down.frag", [GpuFormat.Rgba8Unorm], borrowed: [kit.OneTextureLayout]);
        var set = kit.NewSet(kit.OneTextureLayout);
        ImageSets.Write(gpu.Context, set, [new(kit.LinearSampler, source.View, ImageLayout.ShaderReadOnlyOptimal)]);
        var view = device.Adopt(target.View, target.Image, new Extent2D(Size, Size));
        var commands = new CommandList();
        long allocated = 0;

        gpu.Context.SubmitAndWait(cmd =>
        {
            for (int round = 0; round < 2; round++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 50; i++)
                {
                    commands.Reset();
                    pass.Record(commands, [new ColorTarget(view, LoadOp.DontCare, default)], [device.Adopt(set)], []);
                    device.Play(commands, cmd);
                }

                // The first round warms up; the second is the one a frame pays.
                allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            }
        });

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Validation_reports_nothing_for_a_layer_frame()
    {
        int errors = gpu.ValidationErrors;

        DrawSolid(new Vector4(1f, 0f, 0f, 1f));

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private byte[] DrawSolid(Vector4 color)
    {
        using var device = new VulkanGpu(gpu.Context);
        using var target = Target();
        var view = device.Adopt(target.View, target.Image, new Extent2D(Size, Size));
        var pipeline = device.CreateRenderPipeline(new RenderPipelineDescription
        {
            VertexShader = device.CreateShader("fullscreen.vert"),
            FragmentShader = device.CreateShader("solid_color.frag"),
            ColorFormats = [GpuFormat.Rgba8Unorm],
        });

        var commands = new CommandList();
        commands.BeginRenderPass([new ColorTarget(view, LoadOp.DontCare, default)], null);
        commands.SetPipeline(pipeline);
        commands.SetConstants(MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(in color)));
        commands.Draw(3, 1, 0, 0);
        commands.EndPass();

        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));
        return Read(target);
    }

    private ArgumentException BindToTonemap(int group, string name)
    {
        using var device = new VulkanGpu(gpu.Context);
        using var target = Target();
        var view = device.Adopt(target.View, target.Image, new Extent2D(Size, Size));
        var sampler = device.CreateSampler(new SamplerDescription(SamplerFilter.Linear));
        var pipeline = device.CreateRenderPipeline(new RenderPipelineDescription
        {
            VertexShader = device.CreateShader("fullscreen.vert"),
            FragmentShader = device.CreateShader("tonemap.frag"),
            ColorFormats = [GpuFormat.Rgba8Unorm],
        });

        return Assert.Throws<ArgumentException>(() => device.CreateBindGroup(pipeline, group, [Bind.Image(name, view, sampler)]));
    }

    private byte[] DownsampleTheOldWay(DescriptorKit kit, RenderTarget source)
    {
        using var target = Target();
        using var pass = new FullscreenPass(gpu.Context, "bloom_down.frag", Format.R8G8B8A8Unorm, [kit.OneTextureLayout]);
        var set = kit.NewSet(kit.OneTextureLayout);
        ImageSets.Write(gpu.Context, set, [new(kit.LinearSampler, source.View, ImageLayout.ShaderReadOnlyOptimal)]);
        var api = gpu.Context.Api;
        gpu.Context.SubmitAndWait(cmd =>
        {
            api.BeginColorTarget(cmd, target.Image);
            pass.Record(cmd, target.View, new Extent2D(Size, Size), [set]);
            api.EndColorTarget(cmd, target.Image);
        });
        return Read(target);
    }

    private byte[] DownsampleThroughTheLayer(VulkanGpu device, RenderTarget source)
    {
        using var target = Target();
        using var pass = new FullscreenPass(device, "bloom_down.frag", [GpuFormat.Rgba8Unorm]);
        var sampler = device.CreateSampler(new SamplerDescription(SamplerFilter.Linear));
        var group = device.CreateBindGroup(pass.Pipeline, 0, [Bind.Image("sourceTexture", device.Adopt(source.View), sampler)]);
        var view = device.Adopt(target.View, target.Image, new Extent2D(Size, Size));
        var commands = new CommandList();
        pass.Record(commands, [new ColorTarget(view, LoadOp.DontCare, default)], [group], []);
        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));
        return Read(target);
    }

    // Every pixel different, so filtering between them shows.
    private static byte[] Pattern(int size)
    {
        var bytes = new byte[size * size * 4];
        for (int i = 0; i < size * size; i++)
        {
            bytes[i * 4] = (byte)(i * 4);
            bytes[i * 4 + 1] = (byte)(255 - i * 3);
            bytes[i * 4 + 2] = (byte)(i * 37 % 256);
            bytes[i * 4 + 3] = 255;
        }

        return bytes;
    }

    private RenderTarget Target(int size = Size) => new(
        gpu.Context, Format.R8G8B8A8Unorm, new Extent2D((uint)size, (uint)size),
        ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
        ImageAspectFlags.ColorBit);

    // Leaves the target ready to be read by shaders, as the layer expects.
    private unsafe void Upload(RenderTarget target, int size, byte[] pixels)
    {
        using var buffer = GpuBuffer.CreateHostVisible(gpu.Context, (ulong)pixels.Length, BufferUsageFlags.TransferSrcBit);
        pixels.CopyTo(new Span<byte>(buffer.Mapped, pixels.Length));
        var api = gpu.Context.Api;
        var handle = buffer.Handle;
        var image = target.Image;
        gpu.Context.SubmitAndWait(cmd =>
        {
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.TransferDstOptimal),
                (PipelineStageFlags2.None, AccessFlags2.None), (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit));
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D((uint)size, (uint)size, 1),
            };
            api.CmdCopyBufferToImage(cmd, handle, image, ImageLayout.TransferDstOptimal, 1, &region);
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal),
                (PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit), (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));
        });
    }

    // The layer leaves a target it drew to ready to be read by shaders.
    private unsafe byte[] Read(RenderTarget target)
    {
        using var buffer = GpuBuffer.CreateHostVisible(gpu.Context, Size * Size * 4, BufferUsageFlags.TransferDstBit);
        var api = gpu.Context.Api;
        var handle = buffer.Handle;
        var image = target.Image;
        gpu.Context.SubmitAndWait(cmd =>
        {
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit,
                ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal),
                (PipelineStageFlags2.AllCommandsBit, AccessFlags2.MemoryWriteBit),
                (PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit));
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageExtent = new Extent3D(Size, Size, 1),
            };
            api.CmdCopyImageToBuffer(cmd, image, ImageLayout.TransferSrcOptimal, handle, 1, &region);
        });

        return new ReadOnlySpan<byte>(buffer.Mapped, Size * Size * 4).ToArray();
    }
}
