using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Vulkan;

/// <summary>
/// Plays a <see cref="CommandList"/> into a Vulkan command buffer. Images rest ready to be read by shaders between
/// passes, as the renderer's other passes leave them: <see cref="VulkanTargets"/> moves a render pass's targets to the
/// attachment layouts as it begins and back as it ends, so every image a bind group holds is already readable. Bind groups are bound when a
/// draw needs them, so they can be set before or after the pipeline, as in WebGPU.
/// </summary>
internal sealed unsafe class VulkanPlayer(
    Vk api, (IdTable<AdoptedView> Views, IdTable<AdoptedBuffer> Buffers) resources, IdTable<VulkanPipeline> pipelines, VulkanBindGroups groups)
{
    private const int MaxGroups = 8;

    private readonly VulkanTargets _targets = new(api, resources.Views);
    private readonly BindGroupId[] _bound = new BindGroupId[MaxGroups];
    private int _unbound;
    private VulkanPipeline? _pipeline;
    private bool _computing;

    public void Play(CommandList commands, CommandBuffer cmd)
    {
        // Cleared first, so a list that threw part way leaves nothing behind for the next.
        _pipeline = null;
        Array.Clear(_bound);
        _unbound = 0;
        _targets.Clear();
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
                return _targets.Begin(cmd, words);
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
                return StepGeometry(cmd, command, words);
        }
    }

    /// <summary>The commands that feed draws their vertices and say where on the targets they land.</summary>
    private int StepGeometry(CommandBuffer cmd, Command command, ReadOnlySpan<int> words)
    {
        switch (command)
        {
            case Command.SetVertexBuffer:
                var vertices = resources.Buffers[words[2]].Buffer;
                ulong offset = (ulong)words[3];
                api.CmdBindVertexBuffers(cmd, (uint)words[1], 1, &vertices, &offset);
                return 4;
            case Command.SetIndexBuffer:
                api.CmdBindIndexBuffer(cmd, resources.Buffers[words[1]].Buffer, (ulong)words[2], IndexType.Uint32);
                return 3;
            case Command.DrawIndexed:
                BindGroups(cmd);
                api.CmdDrawIndexed(cmd, (uint)words[1], (uint)words[2], (uint)words[3], words[4], (uint)words[5]);
                return 6;
            case Command.SetViewport:
                var viewport = new Viewport(
                    BitConverter.Int32BitsToSingle(words[1]), BitConverter.Int32BitsToSingle(words[2]),
                    BitConverter.Int32BitsToSingle(words[3]), BitConverter.Int32BitsToSingle(words[4]), 0f, 1f);
                api.CmdSetViewport(cmd, 0, 1, &viewport);
                return 5;
            case Command.SetScissor:
                var scissor = new Rect2D(new Offset2D(words[1], words[2]), new Extent2D((uint)words[3], (uint)words[4]));
                api.CmdSetScissor(cmd, 0, 1, &scissor);
                return 5;
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

    private void EndPass(CommandBuffer cmd)
    {
        if (_computing)
        {
            _computing = false;
            return;
        }

        _targets.End(cmd);
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
