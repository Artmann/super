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

[InlineArray(ProbeCascades.Count)]
internal struct ProbeCascadeOriginArray
{
    private Vector4 _first;
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

    /// <summary>Per cascade, finest first: xyz is the cell of its minimum corner, w its spacing in metres.</summary>
    public ProbeCascadeOriginArray ProbeCascadeOrigins;

    /// <summary>xyz: probes along each axis of every cascade; w: 1 when surfaces may read the cascades.</summary>
    public Vector4 ProbeCascadeCounts;

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

    /// <summary>xyz: the way the wind blows times its strength, zero in a calm; w: its gustiness. See <see cref="Passes.Wind.Pack(WindSettings?, RenderEffects)"/>.</summary>
    public Vector4 Wind;

    /// <summary>xyz: the drawn bake's first light probe; w: their spacing in metres.</summary>
    public Vector4 BakedProbeMin;

    /// <summary>xyz: how many of the bake's probes run along each axis; w: 1 while they light the baked space, 0 for none.</summary>
    public Vector4 BakedProbeCounts;

    /// <summary>xyz: the near corner of the baked space's box, inside which its probes are read; w: the bake's irradiance atlas's slot in the material table.</summary>
    public Vector4 BakedSpaceMin;

    /// <summary>xyz: the box's far corner; w: the bake's depth atlas's slot in the material table.</summary>
    public Vector4 BakedSpaceMax;

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
    /// How many point and spot lights light probe updates: <see cref="LocalLightCount"/> that reach the view, then
    /// the rest of the frame's lights, which the clusters leave out but a probe behind the camera still sees.
    /// </summary>
    public int ProbeLightCount;

    /// <summary>
    /// Last frame's <see cref="Time"/>, which the G-buffer pass sways last frame's grass at for motion vectors. The same
    /// as Time when there's no last frame.
    /// </summary>
    public float PreviousTime;
    public GpuLightArray Lights;
    public GpuReflectionProbeArray Probes;

    /// <summary>World to the light probes' own sun shadow map, which covers the largest cascade. See ProbeSunShadow.</summary>
    public Matrix4x4 ProbeSunMatrix;
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
