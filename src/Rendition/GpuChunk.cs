using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>One placed chunk, as chunks.glsl's ChunkData reads it. Must match it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuChunk
{
    // From the world into the chunk's space, as columns of a mat3x4: column i is row i of the turn, and the move.
    public Vector4 IntoX;
    public Vector4 IntoY;
    public Vector4 IntoZ;
    // The box in the chunk's space, with its edge blend in Min's w and how far its bake has faded in, from 0 to 1, in
    // Max's w.
    public Vector4 Min;
    public Vector4 Max;
    // For each of the frame's four mask slots, the bake's channel that holds it, or -1 when none does.
    public int Mask0, Mask1, Mask2, Mask3;
    public int SkyRed, SkyGreen, SkyBlue, Sun;
    public int Tint, MaskPage, Pad1, Pad2;
}
