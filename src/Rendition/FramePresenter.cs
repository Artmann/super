using System.Diagnostics;
using Silk.NET.Vulkan;
using Rendition.Gpu;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>
/// Takes a frame slot and the surface's image for a frame, submits what it recorded, and shows the image, configuring
/// the surface again when the window has changed.
/// </summary>
internal sealed unsafe class FramePresenter(FrameParts parts, BackgroundBakes background, RendererStats stats)
{
    /// <summary>
    /// Waits until the GPU is done with the frame slot, frees what waited on it and reads back its captures, and takes
    /// the surface's next image. False when the surface no longer fits, after configuring it: then the frame isn't drawn.
    /// </summary>
    public bool Acquire(in FrameSlot frame, in FrameView view, out ViewId image)
    {
        var (stores, targets) = (parts.SceneStores, parts.FrameTargets);
        var context = stores.Context;
        var fence = frame.InFlight;
        long waitStart = Stopwatch.GetTimestamp();
        context.Api.WaitForFences(context.Device, 1, &fence, true, ulong.MaxValue).Check();
        stores.Retired.Release(stores.Ring.Index);
        targets.Surface.Collect(stores.Ring.Index);
        background.Slicer.Timer.Measure(stores.Ring.Index, background.AutoBake);

        if (!targets.Surface.Acquire(stores.Ring.Index, out image))
        {
            targets.Resize((uint)view.Width, (uint)view.Height);
            return false;
        }

        if (stores.Settings.MeasureGpu)
        {
            stats.WaitMilliseconds = Stopwatch.GetElapsedTime(waitStart).TotalMilliseconds;
        }

        return true;
    }

    /// <summary>
    /// Reads the GPU timer's last times for the frame slot, when the GPU is measured, and resets its fence. Returns
    /// whether this frame is timed.
    /// </summary>
    public bool Reset(in FrameSlot frame)
    {
        var (stores, timer) = (parts.SceneStores, parts.FrameStages.GpuTimer);
        bool timed = stores.Settings.MeasureGpu && timer.IsSupported;
        if (timed)
        {
            timer.Collect(stores.Ring.Index);
        }

        var fence = frame.InFlight;
        stores.Context.Api.ResetFences(stores.Context.Device, 1, &fence).Check();
        return timed;
    }

    /// <summary>
    /// Submits the frame's commands, once the surface's image is there, signalling the surface's render-finished
    /// semaphore and the frame's fence.
    /// </summary>
    public void Submit(in FrameSlot frame, CommandBuffer cmd)
    {
        var surface = parts.FrameTargets.Surface;
        var waitInfo = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = surface.ImageAvailable,
            StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
        };
        // Presenting waits for every command, so it comes after the last change of the image's layout
        // too, which the frame's colour output alone doesn't cover, and after the copy of a capture.
        var signalInfo = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = surface.RenderFinished,
            StageMask = PipelineStageFlags2.AllCommandsBit,
        };
        var commandInfo = new CommandBufferSubmitInfo
        {
            SType = StructureType.CommandBufferSubmitInfo,
            CommandBuffer = cmd,
        };
        var submit = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            WaitSemaphoreInfoCount = 1,
            PWaitSemaphoreInfos = &waitInfo,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &commandInfo,
            SignalSemaphoreInfoCount = 1,
            PSignalSemaphoreInfos = &signalInfo,
        };
        var stores = parts.SceneStores;
        stores.Context.Api.QueueSubmit2(stores.Context.Queue, 1, &submit, frame.InFlight).Check();
        stores.Ring.Submitted();
    }

    /// <summary>Shows the image once the frame is drawn, and configures the surface again when the window has changed.</summary>
    public void Present(in FrameView view)
    {
        var targets = parts.FrameTargets;
        if (!targets.Surface.Present())
        {
            targets.Resize((uint)view.Width, (uint)view.Height);
        }
    }
}
