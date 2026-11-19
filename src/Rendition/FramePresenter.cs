using System.Diagnostics;
using Silk.NET.Vulkan;
using Rendition.Vulkan;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Rendition;

/// <summary>
/// Takes a frame slot and a swapchain image for a frame, submits what it recorded, and shows the image, making a new
/// swapchain when the window has changed.
/// </summary>
internal sealed unsafe class FramePresenter(FrameParts parts, BackgroundBakes background, RendererStats stats)
{
    /// <summary>
    /// Waits until the GPU is done with the frame slot, frees what waited on it, and takes the next swapchain image.
    /// False when the swapchain is out of date, after making a new one: then the frame isn't drawn.
    /// </summary>
    public bool Acquire(in FrameSlot frame, in FrameView view, out uint imageIndex)
    {
        var (stores, targets) = (parts.SceneStores, parts.FrameTargets);
        var context = stores.Context;
        var fence = frame.InFlight;
        long waitStart = Stopwatch.GetTimestamp();
        context.Api.WaitForFences(context.Device, 1, &fence, true, ulong.MaxValue).Check();
        stores.Retired.Release(stores.Ring.Index);
        background.Slicer.Timer.Measure(stores.Ring.Index, background.AutoBake);

        uint acquiredIndex = 0;
        var acquired = context.SwapchainApi.AcquireNextImage(
            context.Device, targets.Swapchain.Handle, ulong.MaxValue, frame.ImageAvailable, default, &acquiredIndex);
        imageIndex = acquiredIndex;
        if (acquired == Result.ErrorOutOfDateKhr)
        {
            targets.Resize((uint)view.Width, (uint)view.Height);
            return false;
        }

        if (acquired != Result.SuboptimalKhr)
        {
            acquired.Check();
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
    /// Submits the frame's commands, once the swapchain image is there, signalling <paramref name="renderFinished"/>
    /// and the frame's fence.
    /// </summary>
    public void Submit(in FrameSlot frame, CommandBuffer cmd, Semaphore renderFinished)
    {
        var waitInfo = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = frame.ImageAvailable,
            StageMask = PipelineStageFlags2.ColorAttachmentOutputBit,
        };
        // Presenting waits for every command, so it comes after the last change of the image's layout
        // too, which the frame's colour output alone doesn't cover, and after the copy of a capture.
        var signalInfo = new SemaphoreSubmitInfo
        {
            SType = StructureType.SemaphoreSubmitInfo,
            Semaphore = renderFinished,
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

    /// <summary>Shows the image once the frame is drawn, and makes a new swapchain when the window has changed.</summary>
    public void Present(in FrameView view, uint imageIndex, Semaphore renderFinished)
    {
        var (context, targets) = (parts.SceneStores.Context, parts.FrameTargets);
        var swapchainHandle = targets.Swapchain.Handle;
        var present = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &renderFinished,
            SwapchainCount = 1,
            PSwapchains = &swapchainHandle,
            PImageIndices = &imageIndex,
        };
        var presented = context.SwapchainApi.QueuePresent(context.Queue, &present);
        if (presented is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
        {
            targets.Resize((uint)view.Width, (uint)view.Height);
        }
        else
        {
            presented.Check();
        }
    }
}
