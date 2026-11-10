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

    /// <summary>World to the camera's own space, where it looks down -z.</summary>
    public Matrix4x4 View;

    public Vector4 CameraPosition;

    /// <summary>rgb: background; a: environment intensity.</summary>
    public Vector4 BackgroundRadiance;

    /// <summary>x, y: background fade start and end; z: shadow light angle; w: normal offset.</summary>
    public Vector4 Parameters;

    /// <summary>x: exposure; y: contrast; z: saturation; w: vignette.</summary>
    public Vector4 Grade;

    /// <summary>x: sharpening; y: grain; zw: this frame's anti-aliasing shift.</summary>
    public Vector4 Finish;

    /// <summary>rgb: colour added to the darks; a: how much of the bloom is mixed in.</summary>
    public Vector4 Lift;

    /// <summary>rgb: what each channel is multiplied by.</summary>
    public Vector4 Gain;

    /// <summary>xyz: the point the haze is measured from; w: 1 to measure across the ground only.</summary>
    public Vector4 HazeOrigin;

    /// <summary>rgb: what things fade into; a: 1 to fade into it rather than the background.</summary>
    public Vector4 HazeColor;

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

    /// <summary>
    /// x, y: distance from the camera where shadows start to fade out and where they're gone;
    /// z: the share of a cascade's map, at its edges, over which it hands over to the next;
    /// w: how far contact shadows look along the sun, in metres, with 0 for none.
    /// </summary>
    public Vector4 ShadowFade;

    /// <summary>How many units of depth one unit across each cascade's map covers. Below 1 when a cascade reaches further towards the sun.</summary>
    public Vector4 ShadowDepthScales;

    /// <summary>
    /// How the view's depth is cut into the clusters' slices. x, y: the nearest and furthest
    /// depth any light reaches; z: 1 when the view is parallel, so the slices are even.
    /// </summary>
    public Vector4 Clusters;

    /// <summary>x, y: the size of the render targets in pixels.</summary>
    public Vector4 Screen;

    /// <summary>x: how far ambient occlusion looks, in metres; y: its strength, 0 to 1.</summary>
    public Vector4 Occlusion;

    /// <summary>xyz: the first light probe's position; w: the probes' spacing in metres.</summary>
    public Vector4 LightProbeMin;

    /// <summary>xyz: how many probes run along each axis; w: 1 when surfaces take their soft light from the probes.</summary>
    public Vector4 LightProbeCounts;

    /// <summary>xyz: towards the clear sky's sun; w: its turbidity, 0 for the environment's bands.</summary>
    public Vector4 SkySun;

    /// <summary>rgb: how bright the sun's disc is, 0 for none; a: the cosine of its angular radius.</summary>
    public Vector4 SunDisc;

    /// <summary>x: the fog's density at its height, 0 for none; y: that height; z: its falloff per metre; w: how much it glows towards the suns.</summary>
    public Vector4 Fog;

    /// <summary>The white balance on linear colour, which the tone-mapping pass applies before its curve. See <see cref="PostGrade.WhiteBalance"/>.</summary>
    public Matrix4x4 WhiteBalance;

    /// <summary>x: 1 for the ACES curve, 0 for AgX; y, z: the AgX look's power and saturation. See <see cref="PostGrade.Curve"/>.</summary>
    public Vector4 Curve;

    /// <summary>
    /// x: how far the volumetric fog reaches, 0 for none; y: where its first slice ends; z: its anisotropy;
    /// w: 1 when last frame's volume can be used. See <see cref="VolumetricFog.Pack"/>.
    /// </summary>
    public Vector4 Volumetric;

    /// <summary>x: the water's level; y: the damp band above it; z: 1 when there is water. See <see cref="Passes.Puddles.Pack(float, float)"/>.</summary>
    public Vector4 Puddles;

    public int LightCount;
    public float Time;
    public int FrameNumber;
    public int HistoryValid;
    public int ProbeCount;
    public int ShowEnvironment;
    public int ShadowCascades;
    public int LocalLightCount;

    /// <summary>The <see cref="RenderEffects"/> left out of this frame, which the shaders skip.</summary>
    public int Off;

    /// <summary>
    /// Added to the mip level every material texture is read at. Below 0 while anti-aliasing
    /// shifts the view, since the frames it blends together fill in the finer level.
    /// </summary>
    public float MipBias;

    /// <summary>
    /// 1 while a light probe's faces are drawn: the lighting then writes each pixel's distance
    /// from the probe into alpha, negative for the back of a surface, for probe_fold.comp.
    /// </summary>
    public int ProbeCapture;
    private int _pad;
    public GpuLightArray Lights;
    public GpuReflectionProbeArray Probes;
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

    /// <summary>x: one over the cosines between the inner and outer angles, for the spot's fade; y: its radius; z: how strongly it lights the volumetric fog.</summary>
    public Vector4 Fade;
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
