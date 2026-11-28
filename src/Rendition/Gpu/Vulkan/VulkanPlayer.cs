using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Vulkan;

/// <summary>
/// Plays a <see cref="CommandList"/> into a Vulkan command buffer. Images rest ready to be read by shaders between
/// passes, as the renderer's other passes leave them: a render pass moves its targets to the attachment layout as it
/// begins and back as it ends, so every image a bind group holds is already readable. Bind groups are bound when a
/// draw needs them, so they can be set before or after the pipeline, as in WebGPU.
/// </summary>
internal sealed unsafe class VulkanPlayer(
    Vk api, (IdTable<AdoptedView> Views, IdTable<AdoptedBuffer> Buffers) resources, IdTable<VulkanPipeline> pipelines, VulkanBindGroups groups)
{
    private const int MaxTargets = 8;
    private const int MaxGroups = 8;

    private readonly Image[] _targets = new Image[MaxTargets];
    private readonly BindGroupId[] _bound = new BindGroupId[MaxGroups];
    private int _targetCount;
    private int _unbound;
    private VulkanPipeline? _pipeline;
    private bool _computing;

    public void Play(CommandList commands, CommandBuffer cmd)
    {
        // Cleared first, so a list that threw part way leaves nothing behind for the next.
        _pipeline = null;
        Array.Clear(_bound);
        _unbound = 0;
        _targetCount = 0;
        _computing = false;

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
                api.CmdBindPipeline(cmd, _pipeline.BindPoint, _pipeline.Pipeline);
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
                return StepCompute(cmd, command, words);
        }
    }

    /// <summary>The commands only compute passes and the gaps between passes use.</summary>
    private int StepCompute(CommandBuffer cmd, Command command, ReadOnlySpan<int> words)
    {
        switch (command)
        {
            case Command.BeginComputePass:
                // Vulkan has no compute pass to begin. What drew before is done and seen before the pass reads or
                // writes, and Dispatch keeps each dispatch's writes in order, as WebGPU promises.
                api.MemoryBarrier(cmd, Before, Within);
                _computing = true;
                return 1;
            case Command.Dispatch:
                Dispatch(cmd, words);
                return 4;
            case Command.ClearBuffer:
                ClearBuffer(cmd, words);
                return 4;
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
        var extent = resources.Views[words[2]].Extent;
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
        var view = resources.Views[target[0]];
        if (view.Image.Handle == 0)
        {
            throw new InvalidOperationException($"View {target[0]} was adopted without its image, to be read, so it can't be drawn to.");
        }

        if (view.Storage)
        {
            throw new InvalidOperationException($"View {target[0]} was adopted as Storage, for compute shaders to write, so render passes can't draw to it.");
        }

        var load = (LoadOp)target[1];
        if (view.Surface)
        {
            // Its caller moves it in and out, so EndPass skips it.
            _targets[index] = default;
        }
        else
        {
            MoveToTarget(cmd, view.Image, load);
            _targets[index] = view.Image;
        }
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
        // Compute shaders read resting images too, such as the exposure meter reading the picture, so the waits on
        // both sides of a pass cover them as well as fragment shaders.
        var from = load == LoadOp.Load ? ImageLayout.ShaderReadOnlyOptimal : ImageLayout.Undefined;
        api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit, from, ImageLayout.ColorAttachmentOptimal),
            (Readers, AccessFlags2.ShaderSampledReadBit),
            (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit));
    }

    private const PipelineStageFlags2 Readers = PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit;

    private void EndPass(CommandBuffer cmd)
    {
        if (_computing)
        {
            _computing = false;
            return;
        }

        api.CmdEndRendering(cmd);
        for (int i = 0; i < _targetCount; i++)
        {
            if (_targets[i].Handle != 0)
            {
                api.EndBlendOnto(cmd, _targets[i]);
            }
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
                api.CmdBindDescriptorSets(cmd, _pipeline.BindPoint, _pipeline.Layout, (uint)group, 1, &set, 0, null);
            }
        }

        _unbound = 0;
    }

    /// <summary>
    /// Every stage and access that can touch what a dispatch or a clear wrote, which the barriers after them wait
    /// for. WebGPU orders every dispatch after the one before it, so each dispatch gets a barrier of its own.
    /// </summary>
    private static readonly (PipelineStageFlags2 Stage, AccessFlags2 Access) Users = (
        PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit |
        PipelineStageFlags2.DrawIndirectBit | PipelineStageFlags2.TransferBit,
        AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderSampledReadBit |
        AccessFlags2.IndirectCommandReadBit | AccessFlags2.TransferReadBit | AccessFlags2.TransferWriteBit);

    /// <summary>Everything a compute pass may have to wait for: draws, earlier dispatches and copies, and what they wrote.</summary>
    private static readonly (PipelineStageFlags2 Stage, AccessFlags2 Access) Before = (
        Users.Stage | PipelineStageFlags2.ColorAttachmentOutputBit | PipelineStageFlags2.EarlyFragmentTestsBit |
        PipelineStageFlags2.LateFragmentTestsBit,
        AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentWriteBit | AccessFlags2.ShaderStorageWriteBit |
        AccessFlags2.TransferWriteBit);

    /// <summary>What a compute pass does with what it binds.</summary>
    private static readonly (PipelineStageFlags2 Stage, AccessFlags2 Access) Within = (
        PipelineStageFlags2.ComputeShaderBit,
        AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit |
        AccessFlags2.UniformReadBit);

    private void Dispatch(CommandBuffer cmd, ReadOnlySpan<int> words)
    {
        BindGroups(cmd);
        api.CmdDispatch(cmd, (uint)words[1], (uint)words[2], (uint)words[3]);
        api.MemoryBarrier(cmd, (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit), Users);
    }

    private void ClearBuffer(CommandBuffer cmd, ReadOnlySpan<int> words)
    {
        var buffer = resources.Buffers[words[1]];
        api.MemoryBarrier(cmd, Users, (PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit));
        api.CmdFillBuffer(cmd, buffer.Buffer, (ulong)words[2], (ulong)words[3], 0);
        api.MemoryBarrier(cmd, (PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit), Users);
    }
}
