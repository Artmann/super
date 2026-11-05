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
    private readonly uint _pushConstantSize;

    public FullscreenPass(
        VulkanContext context,
        string fragmentShader,
        Format targetFormat,
        ReadOnlySpan<DescriptorSetLayout> setLayouts,
        uint pushConstantSize = 0)
    {
        _context = context;
        _pushConstantSize = pushConstantSize;

        var pushRange = new PushConstantRange(ShaderStageFlags.FragmentBit, 0, pushConstantSize);
        fixed (DescriptorSetLayout* layouts = setLayouts)
        {
            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
                SetLayoutCount = (uint)setLayouts.Length,
                PSetLayouts = layouts,
                PushConstantRangeCount = pushConstantSize > 0 ? 1u : 0u,
                PPushConstantRanges = &pushRange,
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

    public void Record(CommandBuffer cmd, ImageView target, Extent2D extent, ReadOnlySpan<DescriptorSet> sets) =>
        Record(cmd, target, extent, sets, []);

    /// <summary>Draws with <paramref name="constants"/> as the fragment shader's push constants, which must fill the size given at creation.</summary>
    public void Record(CommandBuffer cmd, ImageView target, Extent2D extent, ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<byte> constants)
    {
        if (constants.Length != (int)_pushConstantSize)
        {
            throw new ArgumentException($"The pass takes {_pushConstantSize} bytes of push constants, not {constants.Length}.", nameof(constants));
        }

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

        if (_pushConstantSize > 0)
        {
            fixed (byte* data = constants)
            {
                api.CmdPushConstants(cmd, _layout, ShaderStageFlags.FragmentBit, 0, _pushConstantSize, data);
            }
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
