using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Vulkan;

/// <summary>
/// The targets of the render pass <see cref="VulkanPlayer"/> is playing. Images rest ready to be read by shaders
/// between passes, so a pass moves its targets to the attachment layouts as it begins and back as it ends.
/// </summary>
internal sealed unsafe class VulkanTargets(Vk api, IdTable<AdoptedView> views)
{
    private const int MaxTargets = 8;

    // Compute shaders read resting images too, such as the exposure meter reading the picture, so the waits on both
    // sides of a pass cover them as well as fragment shaders.
    private const PipelineStageFlags2 Readers = PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit;
    private const PipelineStageFlags2 DepthTests = PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;

    private readonly Image[] _colors = new Image[MaxTargets];
    private int _count;
    private Image _depth;

    /// <summary>Forgets the targets of a list that threw part way.</summary>
    public void Clear()
    {
        _count = 0;
        _depth = default;
    }

    /// <summary>Readies the targets <see cref="CommandList.BeginRenderPass"/> wrote and begins rendering; returns how many words it took.</summary>
    public int Begin(CommandBuffer cmd, ReadOnlySpan<int> words)
    {
        int count = words[1];
        int length = 2 + count * 6 + 4;
        if (count is < 0 or > MaxTargets || (count == 0 && words[length - 4] == 0))
        {
            throw new NotSupportedException($"A render pass takes 0 to {MaxTargets} colour targets, and depth when it has none; this one has {count}.");
        }

        // The pass covers the first target, or the depth when it has no colour, as WebGPU's default viewport does.
        var extent = views[count > 0 ? words[2] : words[length - 4]].Extent;
        var attachments = stackalloc RenderingAttachmentInfo[count];
        for (int i = 0; i < count; i++)
        {
            attachments[i] = Color(cmd, words.Slice(2 + i * 6, 6), i);
        }

        _count = count;
        var depth = Depth(cmd, words.Slice(length - 4, 4));
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, extent),
            LayerCount = 1,
            ColorAttachmentCount = (uint)count,
            PColorAttachments = attachments,
            PDepthAttachment = depth.ImageView.Handle == 0 ? null : &depth,
        };
        api.CmdBeginRendering(cmd, &rendering);
        api.SetFullViewport(cmd, extent);
        return length;
    }

    /// <summary>Ends rendering and moves every target back to resting.</summary>
    public void End(CommandBuffer cmd)
    {
        api.CmdEndRendering(cmd);
        for (int i = 0; i < _count; i++)
        {
            if (_colors[i].Handle != 0)
            {
                api.EndBlendOnto(cmd, _colors[i]);
            }
        }

        if (_depth.Handle != 0)
        {
            api.Transition(cmd, new ImageTransition(_depth, ImageAspectFlags.DepthBit, ImageLayout.DepthAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal),
                (DepthTests, AccessFlags2.DepthStencilAttachmentWriteBit), (Readers, AccessFlags2.ShaderSampledReadBit));
        }

        Clear();
    }

    /// <summary>Readies the colour target that <paramref name="target"/>'s six words describe, the <paramref name="index"/>th of its pass.</summary>
    private RenderingAttachmentInfo Color(CommandBuffer cmd, ReadOnlySpan<int> target, int index)
    {
        var view = views[target[0]];
        if (view.Image.Handle == 0)
        {
            throw new InvalidOperationException($"View {target[0]} was adopted without its image, to be read, so it can't be drawn to.");
        }

        if (view.Storage)
        {
            throw new InvalidOperationException($"View {target[0]} was adopted as Storage, for compute shaders to write, so render passes can't draw to it.");
        }

        var load = (LoadOp)target[1];
        if (view.Held)
        {
            // Its caller moves it in and out, so End skips it.
            _colors[index] = default;
        }
        else
        {
            // What the target held is only kept when the pass loads it; otherwise it is thrown away, which is cheaper.
            api.Transition(cmd, new ImageTransition(view.Image, ImageAspectFlags.ColorBit, Resting(load), ImageLayout.ColorAttachmentOptimal),
                (Readers, AccessFlags2.ShaderSampledReadBit),
                (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit));
            _colors[index] = view.Image;
        }

        return new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = view.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = Of(load),
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(new ClearColorValue(
                BitConverter.Int32BitsToSingle(target[2]), BitConverter.Int32BitsToSingle(target[3]),
                BitConverter.Int32BitsToSingle(target[4]), BitConverter.Int32BitsToSingle(target[5]))),
        };
    }

    /// <summary>
    /// Readies the depth target that <paramref name="target"/>'s four words describe (view, load, clear, read-only),
    /// moving it from resting to drawing as colour targets move, unless it is held. A zero view is no depth.
    /// </summary>
    private RenderingAttachmentInfo Depth(CommandBuffer cmd, ReadOnlySpan<int> target)
    {
        _depth = default;
        if (target[0] == 0)
        {
            return default;
        }

        if (target[3] != 0)
        {
            throw new NotSupportedException("The Vulkan backend of the GPU layer can't test against read-only depth yet.");
        }

        var view = views[target[0]];
        var load = (LoadOp)target[1];
        if (!view.Held)
        {
            // A held view's caller moves it in and out, so End skips it.
            api.Transition(cmd, new ImageTransition(view.Image, ImageAspectFlags.DepthBit, Resting(load), ImageLayout.DepthAttachmentOptimal),
                (Readers, AccessFlags2.ShaderSampledReadBit),
                (DepthTests, AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit));
            _depth = view.Image;
        }

        return new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = view.View,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = Of(load),
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(BitConverter.Int32BitsToSingle(target[2]), 0)),
        };
    }

    /// <summary>The layout a target is moved from: resting when the pass keeps what it held, or nothing in particular.</summary>
    private static ImageLayout Resting(LoadOp load) => load == LoadOp.Load ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.Undefined;

    private static AttachmentLoadOp Of(LoadOp load) => load switch
    {
        LoadOp.Load => AttachmentLoadOp.Load,
        LoadOp.Clear => AttachmentLoadOp.Clear,
        _ => AttachmentLoadOp.DontCare,
    };
}
