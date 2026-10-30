using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rendition.Passes;

[StructLayout(LayoutKind.Sequential)]
internal struct GpuLight
{
    /// <summary>xyz points towards the light; w is 1 when it casts the shadow.</summary>
    public Vector4 Direction;
    public Vector4 Radiance;
}

[InlineArray(FrameUniforms.MaxLights)]
internal struct GpuLightArray
{
    private GpuLight _first;
}

/// <summary>Per-frame shader data. Must match the Frame block in Shaders/common.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameUniforms
{
    public const int MaxLights = 8;

    public Matrix4x4 ViewProjection;
    public Matrix4x4 InverseViewProjection;
    public Matrix4x4 ShadowMatrix;
    public Vector4 CameraPosition;
    public Vector4 AmbientRadiance;
    public Vector4 BackgroundRadiance;

    /// <summary>x, y: background fade start and end; z: shadow light angle; w: normal offset.</summary>
    public Vector4 Parameters;

    public int LightCount;
    private readonly int _padding0;
    private readonly int _padding1;
    private readonly int _padding2;
    public GpuLightArray Lights;
}
