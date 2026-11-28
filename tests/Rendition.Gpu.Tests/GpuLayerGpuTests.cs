using System.Numerics;
using System.Runtime.InteropServices;
using Rendition.Gpu.Vulkan;
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

    private RenderTarget Target() => new(
        gpu.Context, Format.R8G8B8A8Unorm, new Extent2D(Size, Size),
        ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, ImageAspectFlags.ColorBit);

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
