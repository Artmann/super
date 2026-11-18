using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rendition.Passes;

[StructLayout(LayoutKind.Sequential)]
internal struct GpuLight
{
    /// <summary>xyz points towards the light; w is 1 when it casts the shadow.</summary>
    public Vector4 Direction;

    /// <summary>rgb: colour times brightness; w: its channel of the baked shadow mask, or -1 for none.</summary>
    public Vector4 Radiance;
}

/// <summary>One reflection probe as the shaders read it. Must match ReflectionProbe in Shaders/light_data.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuReflectionProbe
{
    /// <summary>xyz is where it was captured; w is the distance over which it fades out.</summary>
    public Vector4 Position;

    /// <summary>xyz: the box's near corner; w: the probe's cube in the cube array.</summary>
    public Vector4 BoxMin;
    public Vector4 BoxMax;
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

[InlineArray(ProbeCascades.Count)]
internal struct ProbeCascadeOriginArray
{
    private Vector4 _first;
}

/// <summary>
/// Per-frame shader data. Must match the Frame block in Shaders/common.glsl, which lists the same fields in the same
/// order; here the runs that belong together are grouped in the blocks of FrameUniformBlocks.cs, at the same offsets.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameUniforms
{
    public const int MaxLights = 8;

    public FrameCameraBlock CameraBlock;

    /// <summary>rgb: background; a: environment intensity.</summary>
    public Vector4 BackgroundRadiance;

    /// <summary>x, y: background fade start and end; z: shadow light angle; w: normal offset.</summary>
    public Vector4 Parameters;

    public FrameGradeBlock GradeBlock;
    public FrameWaterBlock WaterBlock;
    public FrameShadowBlock ShadowBlock;

    /// <summary>
    /// How the view's depth is cut into the clusters' slices. x, y: the nearest and furthest
    /// depth any light reaches; z: 1 when the view is parallel, so the slices are even.
    /// </summary>
    public Vector4 Clusters;

    /// <summary>x, y: the size of the render targets in pixels.</summary>
    public Vector4 Screen;

    /// <summary>x: how far ambient occlusion looks, in metres; y: its strength, 0 to 1.</summary>
    public Vector4 Occlusion;

    public FrameProbeBlock ProbeBlock;
    public FrameSkyBlock SkyBlock;
    public FrameToneBlock ToneBlock;
    public FrameWeatherBlock WeatherBlock;
    public FrameBakedBlock BakedBlock;
    public FrameOutlineBlock OutlineBlock;
    public FrameAirBlock AirBlock;
    public FrameCountBlock CountBlock;
    public GpuLightArray Lights;

    /// <summary>World to the light probes' own sun shadow map, which covers the largest cascade. See ProbeSunShadow.</summary>
    public Matrix4x4 ProbeSunMatrix;

    /// <summary>
    /// World to the sun shadow map round the box of the reflection probe this frame captures, or zero when it captures
    /// none or no sun casts a shadow. See ReflectionCapturePass.
    /// </summary>
    public Matrix4x4 ReflectionSunMatrix;
}

/// <summary>One point or spot light as the shaders read it. Must match LocalLight in Shaders/light_data.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuLocalLight
{
    /// <summary>xyz: where it is; w: its range.</summary>
    public Vector4 PositionRange;

    /// <summary>rgb: colour times brightness; w: its first shadow tile, or -1 for none.</summary>
    public Vector4 RadianceShadow;

    /// <summary>xyz: which way a spot shines; w: the cosine of its outer angle, or -2 for a point light.</summary>
    public Vector4 DirectionCone;

    /// <summary>
    /// x: one over the cosines between the inner and outer angles, for the spot's fade; y: its radius; z: how strongly it
    /// lights the volumetric fog; w: its shadow flags, made by <see cref="ShadowFlags"/>: 1 when it casts a shadow, with a
    /// tile or not, plus twice one more than its channel of the baked shadow mask, so nothing there means no channel.
    /// </summary>
    public Vector4 Fade;

    /// <summary>
    /// Packs whether a light casts a shadow and its channel of the baked shadow mask, -1 for none, into
    /// <see cref="Fade"/>'s w. localCastsShadow and localMaskChannel in Shaders/light_data.glsl read it back.
    /// </summary>
    public static float ShadowFlags(bool castsShadow, int maskChannel) => (castsShadow ? 1 : 0) + 2 * (maskChannel + 1);

    /// <summary>Whether the light casts a shadow, with a tile or not, as <see cref="Fade"/>'s w holds it.</summary>
    public readonly bool CastsShadow => ((int)Fade.W & 1) != 0;

    /// <summary>The light's channel of the baked shadow mask, or -1 for none, as <see cref="Fade"/>'s w holds it.</summary>
    public readonly int MaskChannel => ((int)Fade.W >> 1) - 1;
}

/// <summary>Where one view of a light sits in the shadow atlas. Must match ShadowTile in Shaders/light_data.glsl.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct GpuShadowTile
{
    /// <summary>World to the tile's clip space, reversed-Z between its near and far planes.</summary>
    public Matrix4x4 Matrix;

    /// <summary>xy: the tile's corner in the atlas; zw: its size, both as fractions of the atlas.</summary>
    public Vector4 Rect;

    /// <summary>x, y: the near and far planes; z: the projection's scale, 1 / tan of half the view.</summary>
    public Vector4 Depth;
}
