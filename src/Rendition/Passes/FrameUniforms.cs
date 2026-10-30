using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition.Passes;

/// <summary>Per-frame shader data. Must match the Frame block in Shaders/common.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameUniforms
{
    public Matrix4x4 ViewProjection;
    public Matrix4x4 InverseViewProjection;
    public Vector4 CameraPosition;
    public Vector4 LightDirection;
    public Vector4 LightRadiance;
    public Vector4 AmbientRadiance;
}
