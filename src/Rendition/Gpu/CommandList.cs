namespace Rendition.Gpu;

/// <summary>The commands a <see cref="CommandList"/> records. Each is written as its number followed by its arguments.</summary>
internal enum Command
{
    BeginRenderPass = 1,
    EndPass,
    SetPipeline,
    SetBindGroup,
    SetVertexBuffer,
    SetIndexBuffer,
    SetViewport,
    SetScissor,
    SetConstants,
    Draw,
    DrawIndexed,
    BeginComputePass,
    Dispatch,
    ClearBuffer,
    DrawIndirect,
    CopyBuffer,
    WriteTimestamp,
}

/// <summary>
/// Drawing work written down as ints, for a device to play later. The Vulkan backend plays it
/// into a command buffer; the browser backend hands the whole array to JavaScript in one call.
/// The array is kept between frames, so recording allocates nothing once it has grown.
/// </summary>
internal sealed class CommandList
{
    private readonly CommandWords _words = new();

    /// <summary>Everything recorded since the last <see cref="Reset"/>.</summary>
    public ReadOnlySpan<int> Words => _words.All;

    public void Reset() => _words.Clear();

    /// <summary>
    /// Writes <see cref="Command.BeginRenderPass"/>, the colour target count, six words per colour
    /// target (view, load, clear x, y, z, w) and four for depth (view, load, clear, read-only).
    /// Without depth the four words are zero.
    /// </summary>
    public void BeginRenderPass(ReadOnlySpan<ColorTarget> colors, in DepthTarget? depth)
    {
        _words.Begin(PassKind.Render, nameof(BeginRenderPass));
        var words = _words.Take(2 + colors.Length * 6 + 4);
        words[0] = (int)Command.BeginRenderPass;
        words[1] = colors.Length;
        for (int i = 0; i < colors.Length; i++)
        {
            var color = colors[i];
            var at = words.Slice(2 + i * 6, 6);
            at[0] = color.View.Value;
            at[1] = (int)color.Load;
            at[2] = BitConverter.SingleToInt32Bits(color.Clear.X);
            at[3] = BitConverter.SingleToInt32Bits(color.Clear.Y);
            at[4] = BitConverter.SingleToInt32Bits(color.Clear.Z);
            at[5] = BitConverter.SingleToInt32Bits(color.Clear.W);
        }

        var depthWords = words[^4..];
        depthWords.Clear();
        if (depth is { } target)
        {
            depthWords[0] = target.View.Value;
            depthWords[1] = (int)target.Load;
            depthWords[2] = BitConverter.SingleToInt32Bits(target.Clear);
            depthWords[3] = target.ReadOnly ? 1 : 0;
        }
    }

    public void BeginComputePass()
    {
        _words.Begin(PassKind.Compute, nameof(BeginComputePass));
        _words.Write(Command.BeginComputePass, ReadOnlySpan<int>.Empty);
    }

    public void EndPass()
    {
        _words.End();
        _words.Write(Command.EndPass, ReadOnlySpan<int>.Empty);
    }

    public void SetPipeline(PipelineId pipeline) => _words.Write(Command.SetPipeline, [pipeline.Value]);

    public void SetBindGroup(int group, BindGroupId bindGroup) =>
        _words.Write(Command.SetBindGroup, [group, bindGroup.Value]);

    public void SetVertexBuffer(int slot, BufferId buffer, int offset) =>
        _words.Write(Command.SetVertexBuffer, [slot, buffer.Value, offset]);

    /// <summary>Binds 32-bit indices, the only kind the renderer uses.</summary>
    public void SetIndexBuffer(BufferId buffer, int offset) =>
        _words.Write(Command.SetIndexBuffer, [buffer.Value, offset]);

    /// <summary>
    /// Maps clip space onto this rectangle of the targets, in pixels, with depth kept from 0 to 1. Until a pass sets
    /// one it covers the whole of its first target. Only inside a render pass.
    /// </summary>
    public void SetViewport(float x, float y, float width, float height)
    {
        _words.Require(PassKind.Render, "SetViewport was called outside a render pass.");
        _words.Write(Command.SetViewport, [
            BitConverter.SingleToInt32Bits(x), BitConverter.SingleToInt32Bits(y),
            BitConverter.SingleToInt32Bits(width), BitConverter.SingleToInt32Bits(height)]);
    }

    /// <summary>Draws only inside this rectangle of the targets, in pixels. Until a pass sets one it is the whole of its first target. Only inside a render pass.</summary>
    public void SetScissor(int x, int y, int width, int height)
    {
        _words.Require(PassKind.Render, "SetScissor was called outside a render pass.");
        _words.Write(Command.SetScissor, [x, y, width, height]);
    }

    /// <summary>Writes the byte count and the bytes four to a word in memory order, with the last word padded with zeros.</summary>
    public void SetConstants(ReadOnlySpan<byte> bytes) => _words.Write(Command.SetConstants, bytes);

    public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance) =>
        _words.Write(Command.Draw, [vertexCount, instanceCount, firstVertex, firstInstance]);

    public void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int baseVertex, int firstInstance) =>
        _words.Write(Command.DrawIndexed, [indexCount, instanceCount, firstIndex, baseVertex, firstInstance]);

    /// <summary>
    /// Draws with the vertex count, instance count, first vertex and first instance that four uints of
    /// <paramref name="buffer"/> hold from <paramref name="offset"/>, as a dispatch before the pass wrote them.
    /// </summary>
    public void DrawIndirect(BufferId buffer, int offset)
    {
        _words.Require(PassKind.Render, "DrawIndirect was called outside a render pass.");
        _words.Write(Command.DrawIndirect, [buffer.Value, offset]);
    }

    public void Dispatch(int x, int y, int z) => _words.Write(Command.Dispatch, [x, y, z]);

    /// <summary>
    /// Copies <paramref name="size"/> bytes from <paramref name="source"/> to <paramref name="destination"/>, between
    /// passes, as WebGPU's <c>copyBufferToBuffer</c> does. WebGPU can't write a buffer from inside an encoder, so this
    /// is how a list resets what a dispatch counts into: from a buffer that holds the starting values.
    /// </summary>
    public void CopyBuffer(BufferId source, int sourceOffset, BufferId destination, int destinationOffset, int size)
    {
        _words.Require(PassKind.None, "CopyBuffer was called inside a pass; copy a buffer between passes.");
        _words.Write(Command.CopyBuffer, [source.Value, sourceOffset, destination.Value, destinationOffset, size]);
    }

    /// <summary>Sets <paramref name="size"/> bytes of <paramref name="buffer"/> from <paramref name="offset"/> to zero, between passes, as WebGPU's <c>clearBuffer</c> does.</summary>
    public void ClearBuffer(BufferId buffer, int offset, int size)
    {
        _words.Require(PassKind.None, "ClearBuffer was called inside a pass; clear a buffer between passes.");
        _words.Write(Command.ClearBuffer, [buffer.Value, offset, size]);
    }

    /// <summary>
    /// Writes the time into query <paramref name="index"/> of <paramref name="set"/> once the work before has run,
    /// between passes. WebGPU writes it as the next pass's <c>timestampWrites</c> begin; see "GPU layer" in docs/design.md.
    /// </summary>
    public void WriteTimestamp(QuerySetId set, int index)
    {
        _words.Require(PassKind.None, "WriteTimestamp was called inside a pass; mark the time between passes.");
        _words.Write(Command.WriteTimestamp, [set.Value, index]);
    }
}

/// <summary>Commands made of others.</summary>
internal static class CommandListExtensions
{
    /// <summary>Sets each of <paramref name="bindGroups"/> at its index, where an id of 0 leaves that group unset.</summary>
    public static void SetBindGroups(this CommandList commands, ReadOnlySpan<BindGroupId> bindGroups)
    {
        for (int group = 0; group < bindGroups.Length; group++)
        {
            if (bindGroups[group].Value != 0)
            {
                commands.SetBindGroup(group, bindGroups[group]);
            }
        }
    }
}
