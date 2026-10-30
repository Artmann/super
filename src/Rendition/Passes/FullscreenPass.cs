using Silk.NET.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Runs a fragment shader over the whole target. Both the lighting and the tonemap
/// pass are one of these, and post-processing effects will be too.
/// </summary>
internal sealed unsafe class FullscreenPass : IDisposable
{
    private readonly VulkanContext _context;
    private readonly PipelineLayout _layout;
    private readonly Pipeline _pipeline;

    public FullscreenPass(
        VulkanContext context,
        string fragmentShader,
        Format targetFormat,
        ReadOnlySpan<DescriptorSetLayout> setLayouts)
    {
        _context = context;

        fixed (DescriptorSetLayout* layouts = setLayouts)
        {
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)setLayouts.Length,
                PSetLayouts = layouts,
            };
            context.Api.CreatePipelineLayout(context.Device, &layoutInfo, null, out _layout).Check();
        }

        _pipeline = PipelineFactory.Create(context, new PipelineDescription
        {
            VertexShader = "fullscreen.vert",
            FragmentShader = fragmentShader,
            Layout = _layout,
            ColorFormats = [targetFormat],
            DepthFormat = Format.Undefined,
        });
    }

    public void Record(CommandBuffer cmd, ImageView target, Extent2D extent, ReadOnlySpan<DescriptorSet> sets)
    {
        var api = _context.Api;
        var attachment = new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = target,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.DontCare, // Every pixel gets overwritten.
            StoreOp = AttachmentStoreOp.Store,
        };
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, extent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &attachment,
        };

        api.CmdBeginRendering(cmd, &rendering);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline);
        api.SetFullViewport(cmd, extent);
        fixed (DescriptorSet* setsPtr = sets)
        {
            api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _layout, 0, (uint)sets.Length, setsPtr, 0, null);
        }

        api.CmdDraw(cmd, 3, 1, 0, 0);
        api.CmdEndRendering(cmd);
    }

    public void Dispose()
    {
        _context.Api.DestroyPipeline(_context.Device, _pipeline, null);
        _context.Api.DestroyPipelineLayout(_context.Device, _layout, null);
    }
}
