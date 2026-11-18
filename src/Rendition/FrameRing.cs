namespace Rendition;

/// <summary>
/// The frames in flight: which slot the next frame records into, and how many frames have been submitted. Each slot
/// has its own command buffer, buffers and fence, so the CPU can write one frame while the GPU draws the other.
/// </summary>
internal sealed class FrameRing
{
    /// <summary>How many frames can be in flight at once.</summary>
    public const int Count = 2;

    /// <summary>The slot the frame being recorded uses.</summary>
    public int Index { get; private set; }

    /// <summary>
    /// Frames submitted since the renderer was made, so work that copied results back knows when they have landed.
    /// </summary>
    public long FramesSubmitted { get; private set; }

    /// <summary>The slot whose fence covers the frame most recently submitted.</summary>
    public int LastSubmitted => (Index + Count - 1) % Count;

    /// <summary>Counts a frame submitted.</summary>
    public void Submitted() => FramesSubmitted++;

    /// <summary>Moves on to the next slot, once the frame in this one is submitted.</summary>
    public void MoveToNextSlot() => Index = (Index + 1) % Count;
}
