using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;
using static Rendition.PassStages;

namespace Rendition;

/// <summary>
/// The picture a dissolve, wipe or iris shows over the new frames: the last finished frame, kept when the view asks
/// for a new one, and the transition the finish pass draws with it.
/// </summary>
internal sealed unsafe class HeldPicture(Vk api)
{
    // Which hold the frame asks for, and which hold the held picture belongs to. Whether the held picture and the last
    // finished frame exist yet is on the targets.
    private int _hold;
    private int _heldNumber;

    /// <summary>What the frame's transition asks the finish pass for.</summary>
    public TransitionConstants Transition { get; private set; }

    /// <summary>Takes the frame's transition and the hold it names.</summary>
    public void Take(in ScreenTransition transition)
    {
        Transition = TransitionConstants.From(transition);
        _hold = transition.Hold;
    }

    /// <summary>Keeps the last finished frame when the frame names a new hold. Ldr still holds it, before this frame draws over it.</summary>
    public void Record(CommandBuffer cmd, GBuffer targets)
    {
        if (_hold != _heldNumber || !targets.Picture.HeldReady)
        {
            RecordHold(cmd, targets, copyLastFrame: _hold != _heldNumber && targets.Picture.LdrReady);
            _heldNumber = _hold;
        }
    }

    /// <summary>
    /// Keeps the last finished frame for a dissolve or wipe to show over the new ones,
    /// or black if there is none yet. Leaves both images ready to sample.
    /// </summary>
    private void RecordHold(CommandBuffer cmd, GBuffer targets, bool copyLastFrame)
    {
        // The finish pass sampled both in the frame before.
        api.Transition(cmd, targets.Picture.Held.Image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal, SampledStage, SampledAccess,
            PipelineStageFlags2.CopyBit | PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit);

        if (copyLastFrame)
        {
            api.Transition(cmd, targets.Picture.Ldr.Image, ImageAspectFlags.ColorBit,
                ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal, SampledStage, SampledAccess,
                PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit);

            var region = new ImageCopy
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                Extent = new Extent3D(targets.Picture.Display.Width, targets.Picture.Display.Height, 1),
            };
            api.CmdCopyImage(cmd, targets.Picture.Ldr.Image, ImageLayout.TransferSrcOptimal,
                targets.Picture.Held.Image, ImageLayout.TransferDstOptimal, 1, &region);

            // Tonemapping waits on the fragment stage before it writes Ldr, which comes after this.
            api.Transition(cmd, targets.Picture.Ldr.Image, ImageAspectFlags.ColorBit,
                ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit, SampledStage, AccessFlags2.None);
        }
        else
        {
            var black = new ClearColorValue(0f, 0f, 0f, 1f);
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            api.CmdClearColorImage(cmd, targets.Picture.Held.Image, ImageLayout.TransferDstOptimal, &black, 1, &range);
        }

        api.Transition(cmd, targets.Picture.Held.Image, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.CopyBit | PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit,
            SampledStage, SampledAccess);
        targets.Picture.HeldReady = true;
    }
}
