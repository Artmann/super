using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition.Passes;

/// <summary>Which way the subsurface blur runs. Must match Blur in Shaders/sss_blur.frag.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BlurConstants
{
    /// <summary>xy: one pixel along the blur; z: 1 for the last pass, which adds onto the lit image.</summary>
    public Vector4 Direction;

    public static unsafe uint Size => (uint)sizeof(BlurConstants);

    public static readonly BlurConstants Across = new() { Direction = new Vector4(1f, 0f, 0f, 0f) };
    public static readonly BlurConstants Down = new() { Direction = new Vector4(0f, 1f, 1f, 0f) };
}
