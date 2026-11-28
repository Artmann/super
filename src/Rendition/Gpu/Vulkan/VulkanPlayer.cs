using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Vulkan;

/// <summary>
/// Plays a <see cref="CommandList"/> into a Vulkan command buffer. Images rest ready to be read by shaders between
/// passes, as the renderer's other passes leave them: a render pass moves its targets to the attachment layout as it
/// begins and back as it ends, so every image a bind group holds is already readable. Bind groups are bound when a
/// draw needs them, so they can be set before or after the pipeline, as in WebGPU.
/// </summary>
internal sealed unsafe class VulkanPlayer(Vk api, IdTable<AdoptedView> views, IdTable<VulkanPipeline> pipelines, VulkanBindGroups groups)
{
    private const int MaxTargets = 8;
    private const int MaxGroups = 8;

    private readonly Image[] _targets = new Image[MaxTargets];
    private readonly BindGroupId[] _bound = new BindGroupId[MaxGroups];
    private int _targetCount;
    private int _unbound;
    private VulkanPipeline? _pipeline;

    public void Play(CommandList commands, CommandBuffer cmd)
    {
        // Cleared first, so a list that threw part way leaves nothing behind for the next.
        _pipeline = null;
        Array.Clear(_bound);
        _unbound = 0;
        _targetCount = 0;

        var words = commands.Words;
        int at = 0;
        while (at < words.Length)
        {
            at += Step(cmd, words[at..]);
        }
    }

    /// <summary>Plays the command at the start of <paramref name="words"/> and returns how many words it took.</summary>
    private int Step(CommandBuffer cmd, ReadOnlySpan<int> words)
    {
        var command = (Command)words[0];
        switch (command)
        {
            case Command.BeginRenderPass:
                return BeginRenderPass(cmd, words);
            case Command.EndPass:
                EndPass(cmd);
                return 1;
            case Command.SetPipeline:
                _pipeline = pipelines[words[1]];
                api.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _pipeline.Pipeline);
                _unbound = (1 << MaxGroups) - 1;
                return 2;
            case Command.SetBindGroup:
                _bound[words[1]] = new BindGroupId(words[2]);
                _unbound |= 1 << words[1];
                return 3;
            case Command.SetConstants:
                return SetConstants(cmd, words);
            case Command.Draw:
                BindGroups(cmd);
                api.CmdDraw(cmd, (uint)words[1], (uint)words[2], (uint)words[3], (uint)words[4]);
                return 5;
            case Command.PushLabel:
                return 2;
            case Command.PopLabel:
                return 1;
            default:
                throw new NotSupportedException($"The Vulkan backend of the GPU layer can't play {command} yet.");
        }
    }

    private int BeginRenderPass(CommandBuffer cmd, ReadOnlySpan<int> words)
    {
        int count = words[1];
        if (count is < 1 or > MaxTargets)
        {
            throw new NotSupportedException($"A render pass needs 1 to {MaxTargets} colour targets, not {count}.");
        }

        int length = 2 + count * 6 + 4;
        if (words[length - 4] != 0)
        {
            throw new NotSupportedException("The Vulkan backend of the GPU layer can't draw to depth targets yet.");
        }

        // The pass covers the first target, as WebGPU's default viewport does.
        var extent = views[words[2]].Extent;
        var attachments = stackalloc RenderingAttachmentInfo[count];
        for (int i = 0; i < count; i++)
        {
            attachments[i] = Attachment(cmd, words.Slice(2 + i * 6, 6), i);
        }

        _targetCount = count;
        var rendering = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, extent),
            LayerCount = 1,
            ColorAttachmentCount = (uint)count,
            PColorAttachments = attachments,
        };
        api.CmdBeginRendering(cmd, &rendering);
        api.SetFullViewport(cmd, extent);
        return length;
    }

    /// <summary>Readies the colour target that <paramref name="target"/>'s six words describe, the <paramref name="index"/>th of its pass.</summary>
    private RenderingAttachmentInfo Attachment(CommandBuffer cmd, ReadOnlySpan<int> target, int index)
    {
        var view = views[target[0]];
        if (view.Image.Handle == 0)
        {
            throw new InvalidOperationException($"View {target[0]} was adopted without its image, to be read, so it can't be drawn to.");
        }

        var load = (LoadOp)target[1];
        MoveToTarget(cmd, view.Image, load);
        _targets[index] = view.Image;
        return new RenderingAttachmentInfo
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = view.View,
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = load switch
            {
                LoadOp.Load => AttachmentLoadOp.Load,
                LoadOp.Clear => AttachmentLoadOp.Clear,
                _ => AttachmentLoadOp.DontCare,
            },
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(new ClearColorValue(
                BitConverter.Int32BitsToSingle(target[2]), BitConverter.Int32BitsToSingle(target[3]),
                BitConverter.Int32BitsToSingle(target[4]), BitConverter.Int32BitsToSingle(target[5]))),
        };
    }

    /// <summary>
    /// Moves a target from resting, ready for shaders, to drawing. What it held is only kept when the pass loads it;
    /// otherwise its old contents are thrown away, which is cheaper.
    /// </summary>
    private void MoveToTarget(CommandBuffer cmd, Image image, LoadOp load)
    {
        if (load != LoadOp.Load)
        {
            api.BeginColorTarget(cmd, image);
            return;
        }

        // Compute shaders read resting images too, so the wait covers them as well as fragment shaders.
        api.BeginBlendOnto(cmd, image);
    }

    private void EndPass(CommandBuffer cmd)
    {
        api.CmdEndRendering(cmd);
        for (int i = 0; i < _targetCount; i++)
        {
            api.EndColorTarget(cmd, _targets[i]);
        }

        _targetCount = 0;
    }

    private int SetConstants(CommandBuffer cmd, ReadOnlySpan<int> words)
    {
        int bytes = words[1];
        if (_pipeline is null)
        {
            throw new InvalidOperationException("SetConstants came before any SetPipeline.");
        }

        fixed (int* data = words[2..])
        {
            api.CmdPushConstants(cmd, _pipeline.Layout, _pipeline.ConstantStages, 0, (uint)bytes, data);
        }

        return 2 + (bytes + 3) / 4;
    }

    private void BindGroups(CommandBuffer cmd)
    {
        if (_pipeline is null)
        {
            throw new InvalidOperationException("A draw came before any SetPipeline.");
        }

        for (int group = 0; group < _pipeline.SetLayouts.Length; group++)
        {
            if ((_unbound & (1 << group)) != 0 && _bound[group].Value != 0)
            {
                var set = groups[_bound[group]];
                api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipeline.Layout, (uint)group, 1, &set, 0, null);
            }
        }

        _unbound = 0;
    }
}
