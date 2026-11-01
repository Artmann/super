using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Copies waiting to ride the next frame's command buffer, so creating a mesh or texture
/// during play never waits for the GPU. See docs/design.md on uploads.
/// </summary>
internal sealed unsafe class UploadQueue(VulkanContext context)
{
    private readonly struct Pending(GpuBuffer staging, GpuBuffer? buffer, GpuTexture? texture)
    {
        public readonly GpuBuffer Staging = staging;
        public readonly GpuBuffer? Buffer = buffer;
        public readonly GpuTexture? Texture = texture;
    }

    private readonly List<Pending> _pending = [];

    public bool IsEmpty => _pending.Count == 0;

    public void Add(GpuBuffer staging, GpuBuffer target) => _pending.Add(new Pending(staging, target, null));

    public void Add(GpuBuffer staging, GpuTexture target) => _pending.Add(new Pending(staging, null, target));

    /// <summary>
    /// Records every waiting copy and hands the staging buffers to <paramref name="retire"/>,
    /// which must dispose them once the command buffer has run. Allocates nothing.
    /// </summary>
    public void Record(CommandBuffer cmd, List<IDisposable> retire)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        bool anyBuffer = false;
        foreach (var upload in _pending)
        {
            if (upload.Buffer is { } buffer)
            {
                buffer.RecordUpload(cmd, upload.Staging);
                anyBuffer = true;
            }
            else
            {
                upload.Texture!.RecordUpload(cmd, upload.Staging);
            }

            retire.Add(upload.Staging);
        }

        if (anyBuffer)
        {
            var barrier = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = PipelineStageFlags2.AllTransferBit,
                SrcAccessMask = AccessFlags2.TransferWriteBit,
                DstStageMask = PipelineStageFlags2.VertexInputBit,
                DstAccessMask = AccessFlags2.VertexAttributeReadBit | AccessFlags2.IndexReadBit,
            };
            var dependency = new DependencyInfo
            {
                SType = StructureType.DependencyInfo,
                MemoryBarrierCount = 1,
                PMemoryBarriers = &barrier,
            };
            context.Api.CmdPipelineBarrier2(cmd, &dependency);
        }

        _pending.Clear();
    }

    /// <summary>Runs every waiting copy now and waits for it. For paths that record their own work.</summary>
    public void Flush()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var retired = new List<IDisposable>();
        context.SubmitAndWait(cmd => Record(cmd, retired));
        foreach (var staging in retired)
        {
            staging.Dispose();
        }
    }

    /// <summary>Drops waiting copies without running them. For shutdown only.</summary>
    public void Discard()
    {
        foreach (var upload in _pending)
        {
            upload.Staging.Dispose();
        }

        _pending.Clear();
    }
}
