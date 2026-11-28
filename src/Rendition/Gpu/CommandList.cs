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
    PushLabel,
    PopLabel,
    ClearBuffer,
}

/// <summary>
/// Drawing work written down as ints, for a device to play later. The Vulkan backend plays it
/// into a command buffer; the browser backend hands the whole array to JavaScript in one call.
/// The array is kept between frames, so recording allocates nothing once it has grown.
/// </summary>
internal sealed class CommandList
{
    private readonly CommandWords _words = new();
    private bool _inPass;

    /// <summary>Everything recorded since the last <see cref="Reset"/>.</summary>
    public ReadOnlySpan<int> Words => _words.All;

    /// <summary>The name a <see cref="Command.PushLabel"/> word refers to.</summary>
    public string Label(int index) => _words.Label(index);

    public void Reset()
    {
        _words.Clear();
        _inPass = false;
    }

    /// <summary>
    /// Writes <see cref="Command.BeginRenderPass"/>, the colour target count, six words per colour
    /// target (view, load, clear x, y, z, w) and four for depth (view, load, clear, read-only).
    /// Without depth the four words are zero.
    /// </summary>
    public void BeginRenderPass(ReadOnlySpan<ColorTarget> colors, in DepthTarget? depth)
    {
        if (_inPass)
        {
            throw new InvalidOperationException("BeginRenderPass was called inside a pass that has not ended.");
        }

        _inPass = true;
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
        if (_inPass)
        {
            throw new InvalidOperationException("BeginComputePass was called inside a pass that has not ended.");
        }

        _inPass = true;
        _words.Write(Command.BeginComputePass, ReadOnlySpan<int>.Empty);
    }

    public void EndPass()
    {
        if (!_inPass)
        {
            throw new InvalidOperationException("EndPass was called with no pass begun.");
        }

        _inPass = false;
        _words.Write(Command.EndPass, ReadOnlySpan<int>.Empty);
    }

    public void SetPipeline(PipelineId pipeline) => _words.Write(Command.SetPipeline, [pipeline.Value]);

    public void SetBindGroup(int group, BindGroupId bindGroup) =>
        _words.Write(Command.SetBindGroup, [group, bindGroup.Value]);

    /// <summary>Sets each of <paramref name="bindGroups"/> at its index, where an id of 0 leaves that group unset.</summary>
    public void SetBindGroups(ReadOnlySpan<BindGroupId> bindGroups)
    {
        for (int group = 0; group < bindGroups.Length; group++)
        {
            if (bindGroups[group].Value != 0)
            {
                SetBindGroup(group, bindGroups[group]);
            }
        }
    }

    public void SetVertexBuffer(int slot, BufferId buffer, int offset) =>
        _words.Write(Command.SetVertexBuffer, [slot, buffer.Value, offset]);

    /// <summary>Binds 32-bit indices, the only kind the renderer uses.</summary>
    public void SetIndexBuffer(BufferId buffer, int offset) =>
        _words.Write(Command.SetIndexBuffer, [buffer.Value, offset]);

    /// <summary>Writes the byte count and the bytes four to a word in memory order, with the last word padded with zeros.</summary>
    public void SetConstants(ReadOnlySpan<byte> bytes) => _words.Write(Command.SetConstants, bytes);

    public void Draw(int vertexCount, int instanceCount, int firstVertex, int firstInstance) =>
        _words.Write(Command.Draw, [vertexCount, instanceCount, firstVertex, firstInstance]);

    public void DrawIndexed(int indexCount, int instanceCount, int firstIndex, int baseVertex, int firstInstance) =>
        _words.Write(Command.DrawIndexed, [indexCount, instanceCount, firstIndex, baseVertex, firstInstance]);

    public void Dispatch(int x, int y, int z) => _words.Write(Command.Dispatch, [x, y, z]);

    /// <summary>Sets <paramref name="size"/> bytes of <paramref name="buffer"/> from <paramref name="offset"/> to zero, between passes, as WebGPU's <c>clearBuffer</c> does.</summary>
    public void ClearBuffer(BufferId buffer, int offset, int size)
    {
        if (_inPass)
        {
            throw new InvalidOperationException("ClearBuffer was called inside a pass; clear a buffer between passes.");
        }

        _words.Write(Command.ClearBuffer, [buffer.Value, offset, size]);
    }

    /// <summary>Opens a named group for GPU debuggers. The name is kept beside the words, and the word is its index.</summary>
    public void PushLabel(string name) => _words.Write(Command.PushLabel, [_words.AddLabel(name)]);

    public void PopLabel() => _words.Write(Command.PopLabel, ReadOnlySpan<int>.Empty);
}
