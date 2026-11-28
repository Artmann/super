using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Rendition.Vulkan;

namespace Rendition.Passes;

/// <summary>
/// Runs a fragment shader over the whole target. Both the lighting and the tonemap
/// pass are one of these, and post-processing effects will be too.
/// </summary>
internal sealed unsafe class FullscreenPass : IDisposable
{
    private readonly VulkanContext? _context;
    private readonly PipelineLayout _layout;
    private readonly LivePipeline? _pipeline;
    private readonly GpuDevice? _device;
    private readonly uint _pushConstantSize;
    private readonly int _targetCount;
    private readonly bool _blend;

    /// <summary>
    /// A pass drawn through the GPU layer, writing one output per format in <paramref name="targetFormats"/>. With
    /// <paramref name="blend"/> it adds premultiplied colour over what the targets hold. Until every pass has moved,
    /// <paramref name="borrowed"/> lends the set layouts of groups an old pass fills, which needs a
    /// <see cref="VulkanGpu"/>; see <see cref="VulkanGpu.CreateRenderPipeline(in RenderPipelineDescription, ReadOnlySpan{DescriptorSetLayout})"/>.
    /// </summary>
    public FullscreenPass(
        GpuDevice device,
        string fragmentShader,
        ReadOnlySpan<GpuFormat> targetFormats,
        bool blend = false,
        ReadOnlySpan<DescriptorSetLayout> borrowed = default)
    {
        _device = device;
        _targetCount = targetFormats.Length;
        var description = new RenderPipelineDescription
        {
            VertexShader = device.CreateShader("fullscreen.vert"),
            FragmentShader = device.CreateShader(fragmentShader),
            ColorFormats = targetFormats,
            Blend = blend,
        };
        if (borrowed.IsEmpty)
        {
            Pipeline = device.CreateRenderPipeline(description);
        }
        else
        {
            var vulkan = device as VulkanGpu ?? throw new ArgumentException("Only the Vulkan backend can borrow set layouts.", nameof(borrowed));
            Pipeline = vulkan.CreateRenderPipeline(description, borrowed);
        }
    }

    /// <summary>The pipeline of a pass drawn through the GPU layer, which its bind groups are made for.</summary>
    public PipelineId Pipeline { get; }

    public FullscreenPass(
        VulkanContext context,
        string fragmentShader,
        Format targetFormat,
        ReadOnlySpan<DescriptorSetLayout> setLayouts,
        uint pushConstantSize = 0)
        : this(context, fragmentShader, [targetFormat], setLayouts, pushConstantSize)
    {
    }

    /// <summary>
    /// A pass that writes one output per format in <paramref name="targetFormats"/>. With
    /// <paramref name="blend"/> it adds premultiplied colour over what the targets hold
    /// instead of replacing it.
    /// </summary>
    public FullscreenPass(
        VulkanContext context,
        string fragmentShader,
        ReadOnlySpan<Format> targetFormats,
        ReadOnlySpan<DescriptorSetLayout> setLayouts,
        uint pushConstantSize = 0,
        bool blend = false)
    {
        _context = context;
        _pushConstantSize = pushConstantSize;
        _targetCount = targetFormats.Length;
        _blend = blend;

        PushConstantRange? pushRange = pushConstantSize > 0
            ? new PushConstantRange(ShaderStageFlags.FragmentBit, 0, pushConstantSize)
            : null;
        _layout = context.CreatePipelineLayout(setLayouts, pushRange);

        _pipeline = PipelineFactory.Create(context, new PipelineDescription
        {
            VertexShader = "fullscreen.vert",
            FragmentShader = fragmentShader,
            Layout = _layout,
            ColorFormats = targetFormats,
            DepthFormat = Format.Undefined,
            Transparent = blend,
        });
    }

    public void Record(CommandBuffer cmd, ImageView target, Extent2D extent, ReadOnlySpan<DescriptorSet> sets) =>
        Record(cmd, [target], extent, sets, []);

    /// <summary>Draws with <paramref name="constants"/> as the fragment shader's push constants, which must fill the size given at creation.</summary>
    public void Record(CommandBuffer cmd, ImageView target, Extent2D extent, ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<byte> constants) =>
        Record(cmd, [target], extent, sets, constants);

    /// <summary>Draws into <paramref name="targets"/>, one per format given at creation, in the same order.</summary>
    public void Record(CommandBuffer cmd, ReadOnlySpan<ImageView> targets, Extent2D extent, ReadOnlySpan<DescriptorSet> sets, ReadOnlySpan<byte> constants)
    {
        if (constants.Length != (int)_pushConstantSize)
        {
            throw new ArgumentException($"The pass takes {_pushConstantSize} bytes of push constants, not {constants.Length}.", nameof(constants));
        }

        if (targets.Length != _targetCount)
        {
            throw new ArgumentException($"The pass writes {_targetCount} targets, not {targets.Length}.", nameof(targets));
        }

        var api = _context!.Api;
        Begin(cmd, targets, extent);
        api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline!);
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

    private void Begin(CommandBuffer cmd, ReadOnlySpan<ImageView> targets, Extent2D extent)
    {
        var attachments = stackalloc RenderingAttachmentInfo[_targetCount];
        for (int i = 0; i < _targetCount; i++)
        {
            attachments[i] = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = targets[i],
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                // Blending keeps what is there; otherwise every pixel gets overwritten.
                LoadOp = _blend ? AttachmentLoadOp.Load : AttachmentLoadOp.DontCare,
                StoreOp = AttachmentStoreOp.Store,
            };
        }

        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, extent),
            LayerCount = 1,
            ColorAttachmentCount = (uint)_targetCount,
            PColorAttachments = attachments,
        };

        _context!.Api.CmdBeginRendering(cmd, &rendering);
    }

    /// <summary>
    /// Records the pass into <paramref name="commands"/>: one target per format given at creation, in the same order,
    /// with each of <paramref name="groups"/> set at its index, where an id of 0 leaves that group unset.
    /// </summary>
    public void Record(CommandList commands, ReadOnlySpan<ColorTarget> targets, ReadOnlySpan<BindGroupId> groups, ReadOnlySpan<byte> constants)
    {
        if (targets.Length != _targetCount)
        {
            throw new ArgumentException($"The pass writes {_targetCount} targets, not {targets.Length}.", nameof(targets));
        }

        commands.BeginRenderPass(targets, null);
        commands.SetPipeline(Pipeline);
        for (int group = 0; group < groups.Length; group++)
        {
            if (groups[group].Value != 0)
            {
                commands.SetBindGroup(group, groups[group]);
            }
        }

        if (!constants.IsEmpty)
        {
            commands.SetConstants(constants);
        }

        commands.Draw(3, 1, 0, 0);
        commands.EndPass();
    }

    public void Dispose()
    {
        if (_device is not null)
        {
            _device.Destroy(Pipeline);
            return;
        }

        _pipeline!.Destroy();
        _context!.Api.DestroyPipelineLayout(_context.Device, _layout, null);
    }
}
