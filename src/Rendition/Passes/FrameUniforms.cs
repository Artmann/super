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

[StructLayout(LayoutKind.Sequential)]
internal struct GpuReflectionProbe
{
    /// <summary>xyz is where it was captured; w is the distance over which it fades out.</summary>
    public Vector4 Position;
    public Vector4 BoxMin;
    public Vector4 BoxMax;
}

[InlineArray(FrameUniforms.MaxProbes)]
internal struct GpuReflectionProbeArray
{
    private GpuReflectionProbe _first;
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
    public const int MaxProbes = 4;

    public Matrix4x4 ViewProjection;
    public Matrix4x4 InverseViewProjection;
    public Matrix4x4 ShadowMatrix;

    /// <summary>Last frame's, without its anti-aliasing shift.</summary>
    public Matrix4x4 PreviousViewProjection;

    public Vector4 CameraPosition;

    /// <summary>rgb: background; a: environment intensity.</summary>
    public Vector4 BackgroundRadiance;

    /// <summary>x, y: background fade start and end; z: shadow light angle; w: normal offset.</summary>
    public Vector4 Parameters;

    /// <summary>x: exposure; y: contrast; z: saturation; w: vignette.</summary>
    public Vector4 Grade;

    /// <summary>x: sharpening; y: grain; zw: this frame's anti-aliasing shift.</summary>
    public Vector4 Finish;

    public int LightCount;
    public float Time;
    public int FrameNumber;
    public int HistoryValid;
    public int ProbeCount;
    public int ShowEnvironment;
    private readonly int _padding1;
    private readonly int _padding2;
    public GpuLightArray Lights;
    public GpuReflectionProbeArray Probes;
}
