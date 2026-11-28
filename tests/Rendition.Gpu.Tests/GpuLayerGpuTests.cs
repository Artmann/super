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
    public void A_group_made_again_with_new_images_draws_the_new_ones_without_validation_errors()
    {
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var first = Target(Size * 2);
        using var second = Target(Size * 2);
        Upload(first, Size * 2, Pattern(Size * 2));
        Upload(second, Size * 2, Pattern(Size * 2).Reverse().ToArray());
        using var target = Target();
        using var pass = new FullscreenPass(device, "bloom_down.frag", [GpuFormat.Rgba8Unorm]);
        var sampler = device.CreateSampler(new SamplerDescription(SamplerFilter.Linear));
        var view = device.Adopt(target.View, target.Image, new Extent2D(Size, Size));
        var commands = new CommandList();

        var firstSource = device.Adopt(first.View);
        var group = device.CreateBindGroup(pass.Pipeline, 0, [Bind.Image("sourceTexture", firstSource, sampler)]);
        pass.Record(commands, [new ColorTarget(view, LoadOp.DontCare, default)], [group], []);
        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));
        device.Destroy(group);
        device.Release(firstSource);

        group = device.CreateBindGroup(pass.Pipeline, 0, [Bind.Image("sourceTexture", device.Adopt(second.View), sampler)]);
        commands.Reset();
        pass.Record(commands, [new ColorTarget(view, LoadOp.DontCare, default)], [group], []);
        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));

        // A group made once, fresh, for each source is what the remade group must match.
        Assert.Equal(DownsampleThroughTheLayer(device, second), Read(target));
        Assert.NotEqual(DownsampleThroughTheLayer(device, first), Read(target));
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_pass_that_loads_its_target_keeps_what_it_held_where_it_does_not_draw()
    {
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var target = Target();
        var view = device.Adopt(target.View, target.Image, new Extent2D(Size, Size));
        Upload(target, Size, Pattern(Size));
        var commands = new CommandList();
        // Nothing is drawn, so only the load decides what the target holds afterwards.
        commands.BeginRenderPass([new ColorTarget(view, LoadOp.Load, default)], null);
        commands.EndPass();

        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));

        Assert.Equal(Pattern(Size), Read(target));
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void An_adopted_target_follows_a_new_image_of_the_same_size()
    {
        using var device = new VulkanGpu(gpu.Context);
        using var first = Target();
        using var second = Target();
        using var adopted = new AdoptedTarget(device);
        using var pass = new FullscreenPass(device, "solid_color.frag", [GpuFormat.Rgba8Unorm]);
        var extent = new Extent2D(Size, Size);

        Fill(device, pass, adopted.For(first, extent), new Vector4(1f, 0f, 0f, 1f));
        Assert.Equal(adopted.For(first, extent), adopted.For(first, extent));
        Fill(device, pass, adopted.For(second, extent), new Vector4(0f, 1f, 0f, 1f));

        Assert.Equal([255, 0, 0, 255], Read(first).Take(4));
        Assert.Equal([0, 255, 0, 255], Read(second).Take(4));
    }

    [Fact]
    public void A_held_target_is_drawn_in_the_layout_its_caller_left_it_in()
    {
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var target = Target();
        using var adopted = new AdoptedTarget(device);
        using var pass = new FullscreenPass(device, "solid_color.frag", [GpuFormat.Rgba8Unorm]);
        var color = new Vector4(0f, 0f, 1f, 1f);
        var commands = new CommandList();
        pass.Record(commands, [new ColorTarget(adopted.ForHeld(target.View, target.Image, new Extent2D(Size, Size)), LoadOp.DontCare, default)],
            [], MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(in color)));
        var api = gpu.Context.Api;
        var image = target.Image;

        // As the finish pass does with the window's image: moved in before, and out after, by the caller.
        gpu.Context.SubmitAndWait(cmd =>
        {
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal),
                (PipelineStageFlags2.None, AccessFlags2.None), (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit));
            device.Play(commands, cmd);
            api.EndColorTarget(cmd, image);
        });

        Assert.Equal([0, 0, 255, 255], Read(target).Take(4));
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_held_depth_target_is_tested_in_the_layout_its_caller_left_it_in()
    {
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var target = Target();
        using var depth = LayerImages.Depth(gpu.Context, Size);
        using var adopted = new AdoptedTarget(device);
        using var adoptedDepth = new AdoptedTarget(device);
        var extent = new Extent2D(Size, Size);
        var pipeline = device.CreateRenderPipeline(new RenderPipelineDescription
        {
            VertexShader = device.CreateShader("fullscreen.vert"),
            FragmentShader = device.CreateShader("solid_color.frag"),
            Targets = [new ColorTargetState(GpuFormat.Rgba8Unorm)],
            Depth = new DepthState(GpuFormat.Depth32Float, CompareFunction.Greater, Write: true),
        });
        var color = new Vector4(0f, 1f, 0f, 1f);
        // The depth is cleared to the nearest, so the cover, at depth 0, fails the test and the target stays black.
        var commands = new CommandList();
        commands.BeginRenderPass([new ColorTarget(adopted.For(target, extent), LoadOp.Clear, new Vector4(0f, 0f, 0f, 1f))],
            new DepthTarget(adoptedDepth.ForHeld(depth.View, depth.Image, extent), LoadOp.Clear, 1f, ReadOnly: false));
        commands.SetPipeline(pipeline);
        commands.SetConstants(MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(in color)));
        commands.Draw(3, 1, 0, 0);
        commands.EndPass();
        var api = gpu.Context.Api;
        var image = depth.Image;

        // As the probe spheres do with the scene's depth: moved in before, and out after, by the caller.
        gpu.Context.SubmitAndWait(cmd =>
        {
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.DepthBit, ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal),
                (PipelineStageFlags2.None, AccessFlags2.None),
                (PipelineStageFlags2.EarlyFragmentTestsBit, AccessFlags2.DepthStencilAttachmentWriteBit));
            device.Play(commands, cmd);
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.DepthBit, ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal),
                (PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentWriteBit),
                (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));
        });

        Assert.Equal([0, 0, 0, 255], Read(target).Take(4));
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_depth_only_pass_clears_the_part_of_its_target_inside_the_viewport_to_the_far_plane()
    {
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var target = Target();
        using var depth = LayerImages.Depth(gpu.Context, Size);
        using var adopted = new AdoptedTarget(device);
        var extent = new Extent2D(Size, Size);
        var depthView = device.Adopt(depth.View, depth.Image, extent);
        var halve = HalfFarDepth(device, depthView);
        var cover = CoverAtFarPlane(device, adopted.For(target, extent), new DepthTarget(depthView, LoadOp.Load, 0f, ReadOnly: false));

        gpu.Context.SubmitAndWait(cmd =>
        {
            device.Play(halve, cmd);
            device.Play(cover, cmd);
        });

        AssertLeftHalfGreen(target);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_read_only_depth_target_is_tested_and_left_resting_for_the_next_pass()
    {
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var target = Target();
        using var depth = LayerImages.Depth(gpu.Context, Size);
        using var adopted = new AdoptedTarget(device);
        var extent = new Extent2D(Size, Size);
        var depthView = device.Adopt(depth.View, depth.Image, extent);
        var halve = HalfFarDepth(device, depthView);
        var cover = CoverAtFarPlane(device, adopted.For(target, extent), new DepthTarget(depthView, LoadOp.Load, 0f, ReadOnly: true));

        // Played twice, so the second begins from where the first left the depth.
        gpu.Context.SubmitAndWait(cmd =>
        {
            device.Play(halve, cmd);
            device.Play(cover, cmd);
            device.Play(cover, cmd);
        });

        AssertLeftHalfGreen(target);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_held_read_only_depth_target_is_tested_in_the_read_only_layout_its_caller_left_it_in()
    {
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var target = Target();
        using var depth = LayerImages.Depth(gpu.Context, Size);
        using var adopted = new AdoptedTarget(device);
        using var adoptedDepth = new AdoptedTarget(device);
        var extent = new Extent2D(Size, Size);
        var depthView = adoptedDepth.ForHeld(depth.View, depth.Image, extent);
        var halve = HalfFarDepth(device, depthView);
        var cover = CoverAtFarPlane(device, adopted.For(target, extent), new DepthTarget(depthView, LoadOp.Load, 0f, ReadOnly: true));
        var api = gpu.Context.Api;
        var image = depth.Image;

        // As the transparent pass does with the scene's depth: drawn, then moved to read-only for glass, by the caller.
        gpu.Context.SubmitAndWait(cmd =>
        {
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.DepthBit, ImageLayout.Undefined, ImageLayout.DepthAttachmentOptimal),
                (PipelineStageFlags2.None, AccessFlags2.None),
                (PipelineStageFlags2.EarlyFragmentTestsBit, AccessFlags2.DepthStencilAttachmentWriteBit));
            device.Play(halve, cmd);
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.DepthBit, ImageLayout.DepthAttachmentOptimal, ImageLayout.DepthReadOnlyOptimal),
                (PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentWriteBit),
                (PipelineStageFlags2.EarlyFragmentTestsBit, AccessFlags2.DepthStencilAttachmentReadBit));
            device.Play(cover, cmd);
            api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.DepthBit, ImageLayout.DepthReadOnlyOptimal, ImageLayout.ShaderReadOnlyOptimal),
                (PipelineStageFlags2.LateFragmentTestsBit, AccessFlags2.DepthStencilAttachmentReadBit),
                (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));
        });

        AssertLeftHalfGreen(target);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_read_only_depth_target_that_does_not_load_throws()
    {
        using var device = new VulkanGpu(gpu.Context);
        using var depth = LayerImages.Depth(gpu.Context, Size);
        var depthView = device.Adopt(depth.View, depth.Image, new Extent2D(Size, Size));
        var commands = new CommandList();
        commands.BeginRenderPass([], new DepthTarget(depthView, LoadOp.Clear, 0f, ReadOnly: true));
        commands.EndPass();

        var thrown = Assert.Throws<InvalidOperationException>(() => gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd)));
        Assert.Equal("A read-only depth target can't be cleared or thrown away, since nothing is written to it; load it.", thrown.Message);
    }

    [Fact]
    public void A_pass_with_two_targets_writes_both()
    {
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var first = Target();
        using var second = Target();
        using var firstTarget = new AdoptedTarget(device);
        using var secondTarget = new AdoptedTarget(device);
        using var pass = new FullscreenPass(device, "solid_colors.frag", [GpuFormat.Rgba8Unorm, GpuFormat.Rgba8Unorm]);
        var extent = new Extent2D(Size, Size);
        ReadOnlySpan<Vector4> colors = [new(1f, 0f, 0f, 1f), new(0f, 0f, 1f, 1f)];
        var commands = new CommandList();
        pass.Record(commands,
            [new ColorTarget(firstTarget.For(first, extent), LoadOp.DontCare, default), new ColorTarget(secondTarget.For(second, extent), LoadOp.DontCare, default)],
            [], MemoryMarshal.AsBytes(colors));

        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));

        Assert.Equal([255, 0, 0, 255], Read(first).Take(4));
        Assert.Equal([0, 0, 255, 255], Read(second).Take(4));
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_blended_pass_adds_onto_what_the_target_held()
    {
        using var device = new VulkanGpu(gpu.Context);
        using var target = Target();
        Upload(target, Size, Pattern(Size));
        var view = device.Adopt(target.View, target.Image, new Extent2D(Size, Size));
        using var pass = new FullscreenPass(device, "solid_color.frag", [GpuFormat.Rgba8Unorm], blend: true);
        var color = new Vector4(0.25f, 0f, 0f, 0f);
        var commands = new CommandList();
        pass.Record(commands, [new ColorTarget(view, LoadOp.Load, default)], [], MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(in color)));

        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));

        byte[] pattern = Pattern(Size);
        byte[] pixels = Read(target);
        for (int i = 0; i < Size * Size; i++)
        {
            // Premultiplied over: the colour plus what was there times one minus its alpha of 0.
            Assert.InRange(pixels[i * 4], Math.Min(255, pattern[i * 4] + 63), Math.Min(255, pattern[i * 4] + 65));
            Assert.Equal(pattern[i * 4 + 1], pixels[i * 4 + 1]);
        }
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
    public void A_sampler_asked_for_twice_is_made_once_and_a_different_one_is_made_apart()
    {
        using var device = new VulkanGpu(gpu.Context);
        var linear = device.CreateSampler(new SamplerDescription(SamplerFilter.Linear));

        Assert.Equal(linear, device.CreateSampler(new SamplerDescription(SamplerFilter.Linear)));
        Assert.NotEqual(linear, device.CreateSampler(new SamplerDescription(SamplerFilter.Nearest)));
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
        long allocated = LayerImages.AllocatedPlaying(gpu.Context, device, commands =>
            pass.Record(commands, [new ColorTarget(view, LoadOp.DontCare, default)], [device.Adopt(set)], []));

        Assert.Equal(0, allocated);
    }

    [Fact]
    public void Validation_reports_nothing_for_a_layer_frame()
    {
        int errors = gpu.ValidationErrors;

        DrawSolid(new Vector4(1f, 0f, 0f, 1f));

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_compute_pass_writes_a_storage_image()
    {
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var image = StorageTarget();
        var view = device.Adopt(new AdoptedView(image.View, image.Image, new Extent2D(Size, Size), Storage: true));
        using var pass = new ComputePass(device, "test_fill.comp");
        var group = device.CreateBindGroup(pass.Pipeline, 0, [Bind.StorageImage("outputImage", view)]);
        var commands = new CommandList();
        pass.Record(commands, [group], MemoryMarshal.AsBytes<float>([1f / 255f, 0f]), (1, 1, 1));

        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));

        byte[] pixels = Read(image, ImageLayout.General);
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                Assert.Equal([(byte)x, (byte)y, 0, 255], pixels.AsSpan((y * Size + x) * 4, 4).ToArray());
            }
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    /// <summary>
    /// The second step reads from the far end what the first writes after working a while, so without a barrier its
    /// first workgroups read before the first step's last ones have written.
    /// </summary>
    [Fact]
    public unsafe void A_dispatch_sees_what_the_dispatch_before_it_wrote()
    {
        const int count = 64 * 1024;
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var first = GpuBuffer.CreateHostVisible(gpu.Context, count * 4, BufferUsageFlags.StorageBufferBit);
        using var second = GpuBuffer.CreateHostVisible(gpu.Context, count * 4, BufferUsageFlags.StorageBufferBit);
        new Span<uint>(first.Mapped, count).Clear();
        using var pass = new ComputePass(device, "test_steps.comp");
        var group = device.CreateBindGroup(pass.Pipeline, 0,
            [Bind.Buffer("First", device.Adopt(first.Handle, count * 4)), Bind.Buffer("Second", device.Adopt(second.Handle, count * 4))]);
        var commands = new CommandList();
        pass.Record(commands, [group], MemoryMarshal.AsBytes<uint>([0u, 4096u]), (count / 64, 1, 1));
        pass.Record(commands, [group], MemoryMarshal.AsBytes<uint>([1u, 0u]), (count / 64, 1, 1));

        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));

        var doubled = new ReadOnlySpan<uint>(second.Mapped, count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal((uint)((count - 1 - i) * 2), doubled[i]);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public unsafe void A_cleared_buffer_reads_zero_where_it_was_cleared()
    {
        using var device = new VulkanGpu(gpu.Context);
        using var buffer = GpuBuffer.CreateHostVisible(gpu.Context, 1024, BufferUsageFlags.TransferDstBit | BufferUsageFlags.StorageBufferBit);
        var bytes = new Span<byte>(buffer.Mapped, 1024);
        bytes.Fill(0xFF);
        var commands = new CommandList();
        commands.ClearBuffer(device.Adopt(buffer.Handle, 1024), 256, 256);

        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));

        Assert.All(bytes[..256].ToArray(), value => Assert.Equal(0xFF, value));
        Assert.All(bytes[256..512].ToArray(), value => Assert.Equal(0, value));
        Assert.All(bytes[512..].ToArray(), value => Assert.Equal(0xFF, value));
    }

    [Fact]
    public unsafe void A_copied_buffer_holds_the_source_bytes_only_where_they_were_copied()
    {
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var source = GpuBuffer.CreateHostVisible(gpu.Context, 64, BufferUsageFlags.TransferSrcBit);
        using var destination = GpuBuffer.CreateHostVisible(gpu.Context, 64, BufferUsageFlags.TransferDstBit);
        var from = new Span<byte>(source.Mapped, 64);
        for (int i = 0; i < 64; i++)
        {
            from[i] = (byte)(i + 1);
        }

        var to = new Span<byte>(destination.Mapped, 64);
        to.Clear();
        var commands = new CommandList();
        commands.CopyBuffer(device.Adopt(source.Handle, 64), 8, device.Adopt(destination.Handle, 64), 16, 16);

        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));

        // Bytes 8 to 24 of the source hold 9 to 24, and land at 16 to 32.
        Assert.All(to[..16].ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(Enumerable.Range(9, 16).Select(i => (byte)i).ToArray(), to[16..32].ToArray());
        Assert.All(to[32..].ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Copying_a_buffer_inside_a_pass_throws()
    {
        var commands = new CommandList();
        commands.BeginComputePass();

        var thrown = Assert.Throws<InvalidOperationException>(() => commands.CopyBuffer(new BufferId(1), 0, new BufferId(2), 0, 4));
        Assert.Equal("CopyBuffer was called inside a pass; copy a buffer between passes.", thrown.Message);
    }

    [Fact]
    public void Clearing_a_buffer_inside_a_pass_throws()
    {
        var commands = new CommandList();
        commands.BeginComputePass();

        var thrown = Assert.Throws<InvalidOperationException>(() => commands.ClearBuffer(new BufferId(1), 0, 4));
        Assert.Equal("ClearBuffer was called inside a pass; clear a buffer between passes.", thrown.Message);
    }

    [Fact]
    public void A_storage_image_can_be_sampled_by_the_next_pass()
    {
        int errors = gpu.ValidationErrors;
        using var device = new VulkanGpu(gpu.Context);
        using var image = StorageTarget();
        using var target = Target();
        var extent = new Extent2D(Size, Size);
        var storage = device.Adopt(new AdoptedView(image.View, image.Image, extent, Storage: true));
        using var fill = new ComputePass(device, "test_fill.comp");
        using var sample = new FullscreenPass(device, "bloom_down.frag", [GpuFormat.Rgba8Unorm]);
        var sampler = device.CreateSampler(new SamplerDescription(SamplerFilter.Linear));
        var fillGroup = device.CreateBindGroup(fill.Pipeline, 0, [Bind.StorageImage("outputImage", storage)]);
        var sampleGroup = device.CreateBindGroup(sample.Pipeline, 0, [Bind.Image("sourceTexture", storage, sampler)]);
        var commands = new CommandList();
        fill.Record(commands, [fillGroup], MemoryMarshal.AsBytes<float>([0f, 0.5f]), (1, 1, 1));
        sample.Record(commands, [new ColorTarget(device.Adopt(target.View, target.Image, extent), LoadOp.DontCare, default)], [sampleGroup], []);

        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));

        byte[] pixels = Read(target);
        for (int i = 0; i < Size * Size; i++)
        {
            Assert.Equal(0, pixels[i * 4]);
            Assert.InRange(pixels[i * 4 + 2], 127, 128);
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void A_repeating_sampler_wraps_past_the_edge_and_a_clamping_one_does_not()
    {
        using var device = new VulkanGpu(gpu.Context);
        using var source = Target();
        // Black but for a red right-hand column.
        var pixels = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            pixels[(y * Size + Size - 1) * 4] = 255;
        }

        Upload(source, Size, pixels);

        // Each pixel in the left column takes two of its four taps at the middle of the texel one past the left edge.
        // Wrapped, that is the red column, so red is 255 * 2 / 4, about 127; clamped, it is the black left column.
        byte[] repeated = DownsampleThroughTheLayer(device, source, SamplerAddress.Repeat);
        byte[] clamped = DownsampleThroughTheLayer(device, source, SamplerAddress.Clamp);
        for (int y = 0; y < Size; y++)
        {
            Assert.InRange(repeated[y * Size * 4], 126, 128);
            Assert.Equal(0, clamped[y * Size * 4]);
        }
    }

    [Fact]
    public void A_render_pass_refuses_a_storage_view()
    {
        using var device = new VulkanGpu(gpu.Context);
        using var image = StorageTarget();
        var view = device.Adopt(new AdoptedView(image.View, image.Image, new Extent2D(Size, Size), Storage: true));
        using var pass = new FullscreenPass(device, "solid_color.frag", [GpuFormat.Rgba8Unorm]);
        var commands = new CommandList();
        var color = Vector4.One;
        pass.Record(commands, [new ColorTarget(view, LoadOp.DontCare, default)], [], MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(in color)));

        var thrown = Assert.Throws<InvalidOperationException>(() => gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd)));
        Assert.Contains("adopted as Storage", thrown.Message);
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
            Targets = [new ColorTargetState(GpuFormat.Rgba8Unorm)],
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

    // Clears the depth to the nearest, then its left half back to the far plane, as a shadow tile is cleared.
    private static CommandList HalfFarDepth(VulkanGpu device, ViewId depth)
    {
        var clear = device.CreateRenderPipeline(new RenderPipelineDescription
        {
            VertexShader = device.CreateShader("depth_clear.vert"),
            Targets = [],
            Depth = new DepthState(GpuFormat.Depth32Float, CompareFunction.Always, Write: true),
        });
        var commands = new CommandList();
        commands.BeginRenderPass([], new DepthTarget(depth, LoadOp.Clear, 1f, ReadOnly: false));
        commands.SetViewport(0, 0, Size / 2, Size);
        commands.SetScissor(0, 0, Size / 2, Size);
        commands.SetPipeline(clear);
        commands.Draw(3, 1, 0, 0);
        commands.EndPass();
        return commands;
    }

    // Clears the target to black and covers it in green at the far plane, which passes only where the depth is there too.
    private static CommandList CoverAtFarPlane(VulkanGpu device, ViewId target, in DepthTarget depth)
    {
        var cover = device.CreateRenderPipeline(new RenderPipelineDescription
        {
            VertexShader = device.CreateShader("fullscreen.vert"),
            FragmentShader = device.CreateShader("solid_color.frag"),
            Targets = [new ColorTargetState(GpuFormat.Rgba8Unorm)],
            Depth = new DepthState(GpuFormat.Depth32Float, CompareFunction.GreaterEqual, Write: false),
        });
        var color = new Vector4(0f, 1f, 0f, 1f);
        var commands = new CommandList();
        commands.BeginRenderPass([new ColorTarget(target, LoadOp.Clear, new Vector4(0f, 0f, 0f, 1f))], depth);
        commands.SetPipeline(cover);
        commands.SetConstants(MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(in color)));
        commands.Draw(3, 1, 0, 0);
        commands.EndPass();
        return commands;
    }

    private void AssertLeftHalfGreen(RenderTarget target)
    {
        byte[] pixels = Read(target);
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                byte[] expected = x < Size / 2 ? [0, 255, 0, 255] : [0, 0, 0, 255];
                Assert.Equal(expected, pixels.AsSpan((y * Size + x) * 4, 4).ToArray());
            }
        }
    }

    private void Fill(VulkanGpu device, FullscreenPass pass, ViewId view, Vector4 color)
    {
        var commands = new CommandList();
        pass.Record(commands, [new ColorTarget(view, LoadOp.DontCare, default)], [], MemoryMarshal.AsBytes(new ReadOnlySpan<Vector4>(in color)));
        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));
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
            Targets = [new ColorTargetState(GpuFormat.Rgba8Unorm)],
        });

        return Assert.Throws<ArgumentException>(() => device.CreateBindGroup(pipeline, group, [Bind.Image(name, view, sampler)]));
    }

    private byte[] DownsampleThroughTheLayer(VulkanGpu device, RenderTarget source, SamplerAddress address = SamplerAddress.Clamp)
    {
        using var target = Target();
        using var pass = new FullscreenPass(device, "bloom_down.frag", [GpuFormat.Rgba8Unorm]);
        var sampler = device.CreateSampler(new SamplerDescription(SamplerFilter.Linear, address));
        var group = device.CreateBindGroup(pass.Pipeline, 0, [Bind.Image("sourceTexture", device.Adopt(source.View), sampler)]);
        var view = device.Adopt(target.View, target.Image, new Extent2D(Size, Size));
        var commands = new CommandList();
        pass.Record(commands, [new ColorTarget(view, LoadOp.DontCare, default)], [group], []);
        gpu.Context.SubmitAndWait(cmd => device.Play(commands, cmd));
        return Read(target);
    }

    private static byte[] Pattern(int size) => LayerImages.Pattern(size);

    private RenderTarget Target(int size = Size) => LayerImages.Color(gpu.Context, size);

    // An image compute writes, left resting in General, as a Storage view expects.
    private RenderTarget StorageTarget()
    {
        var target = new RenderTarget(
            gpu.Context, Format.R8G8B8A8Unorm, new Extent2D(Size, Size),
            ImageUsageFlags.StorageBit | ImageUsageFlags.TransferSrcBit | ImageUsageFlags.SampledBit, ImageAspectFlags.ColorBit);
        var api = gpu.Context.Api;
        gpu.Context.SubmitAndWait(cmd => api.Transition(cmd,
            new ImageTransition(target.Image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.General),
            (PipelineStageFlags2.None, AccessFlags2.None), (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit)));
        return target;
    }

    private void Upload(RenderTarget target, int size, byte[] pixels) => LayerImages.Upload(gpu.Context, target, size, pixels);

    private byte[] Read(RenderTarget target, ImageLayout resting = ImageLayout.ShaderReadOnlyOptimal) =>
        LayerImages.Read(gpu.Context, target, Size, resting);
}
