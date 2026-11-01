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

[InlineArray(ShadowPass.MaxCascades)]
internal struct ShadowMatrixArray
{
    private Matrix4x4 _first;
}

/// <summary>Per-frame shader data. Must match the Frame block in Shaders/common.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameUniforms
{
    public const int MaxLights = 8;
    public const int MaxProbes = 4;

    public Matrix4x4 ViewProjection;
    public Matrix4x4 InverseViewProjection;
    public ShadowMatrixArray ShadowMatrices;

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

    /// <summary>rgb: light lost per metre; a: 1 when the view is underwater.</summary>
    public Vector4 WaterAbsorption;

    /// <summary>rgb: colour of the water looking up; a: height of the surface.</summary>
    public Vector4 WaterShallow;

    /// <summary>rgb: colour of the water looking down; a: strength of the light shafts.</summary>
    public Vector4 WaterDeep;

    /// <summary>x: strength of the caustics; y: how small they are; z: reach of the shafts.</summary>
    public Vector4 WaterLight;

    /// <summary>The radius each shadow cascade covers, in metres.</summary>
    public Vector4 ShadowRadii;

    public int LightCount;
    public float Time;
    public int FrameNumber;
    public int HistoryValid;
    public int ProbeCount;
    public int ShowEnvironment;
    public int ShadowCascades;
    private readonly int _padding;
    public GpuLightArray Lights;
    public GpuReflectionProbeArray Probes;
}
