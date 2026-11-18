using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition.Passes;

// The runs of FrameUniforms that belong together. Every field in them is four-byte aligned, so grouping them moves no
// offset: the Frame block in Shaders/common.glsl still reads each where it was, which PostGradeTests checks.

/// <summary>The camera: where it is, how it projects this frame and last, and the sun's cascade matrices.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameCameraBlock
{
    public Matrix4x4 ViewProjection;
    public Matrix4x4 InverseViewProjection;
    public ShadowMatrixArray ShadowMatrices;

    /// <summary>Last frame's, without its anti-aliasing shift.</summary>
    public Matrix4x4 PreviousViewProjection;

    /// <summary>World to the camera's own space, where it looks down -z.</summary>
    public Matrix4x4 View;

    public Vector4 CameraPosition;
}

/// <summary>The grade and finish, and the haze things fade into.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameGradeBlock
{
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
}

/// <summary>The water the view may be under.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameWaterBlock
{
    /// <summary>rgb: light lost per metre; a: 1 when the view is underwater.</summary>
    public Vector4 WaterAbsorption;

    /// <summary>rgb: colour of the water looking up; a: height of the surface.</summary>
    public Vector4 WaterShallow;

    /// <summary>rgb: colour of the water looking down; a: strength of the light shafts.</summary>
    public Vector4 WaterDeep;

    /// <summary>x: strength of the caustics; y: how small they are; z: reach of the shafts.</summary>
    public Vector4 WaterLight;
}

/// <summary>The sun's shadow cascades.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameShadowBlock
{
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
}

/// <summary>The live light probes: the fixed grid and the cascades.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameProbeBlock
{
    /// <summary>xyz: the first light probe's position; w: the probes' spacing in metres.</summary>
    public Vector4 LightProbeMin;

    /// <summary>xyz: how many probes run along each axis; w: 1 when surfaces take their soft light from the probes.</summary>
    public Vector4 LightProbeCounts;

    /// <summary>Per cascade, finest first: xyz is the cell of its minimum corner, w its spacing in metres.</summary>
    public ProbeCascadeOriginArray ProbeCascadeOrigins;

    /// <summary>xyz: probes along each axis of every cascade; w: 1 when surfaces may read the cascades.</summary>
    public Vector4 ProbeCascadeCounts;
}

/// <summary>The clear sky's sun and its disc, and the fog.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameSkyBlock
{
    /// <summary>xyz: towards the clear sky's sun; w: its turbidity, 0 for the environment's bands.</summary>
    public Vector4 SkySun;

    /// <summary>rgb: how bright the sun's disc is, 0 for none; a: the cosine of its angular radius.</summary>
    public Vector4 SunDisc;

    /// <summary>x: the fog's density at its height, 0 for none; y: that height; z: its falloff per metre; w: how much it glows towards the suns.</summary>
    public Vector4 Fog;
}

/// <summary>What the tone-mapping pass applies.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameToneBlock
{
    /// <summary>The white balance on linear colour, which the tone-mapping pass applies before its curve. See <see cref="PostGrade.WhiteBalance"/>.</summary>
    public Matrix4x4 WhiteBalance;

    /// <summary>x: 1 for the ACES curve, 0 for AgX; y, z: the AgX look's power and saturation. See <see cref="PostGrade.Curve"/>.</summary>
    public Vector4 Curve;
}

/// <summary>The volumetric fog, the puddles and the wind.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameWeatherBlock
{
    /// <summary>
    /// x: how far the volumetric fog reaches, 0 for none; y: where its first slice ends; z: its anisotropy;
    /// w: 1 when last frame's volume can be used. See <see cref="VolumetricFog.Pack"/>.
    /// </summary>
    public Vector4 Volumetric;

    /// <summary>x: the volumetric fog's slices in depth; y: scene pixels across a cell; z: how many fog volumes are drawn.</summary>
    public Vector4 VolumetricGrid;

    /// <summary>x: the water's level; y: the damp band above it; z: 1 when there is water. See <see cref="Passes.Puddles.Pack(float, float)"/>.</summary>
    public Vector4 Puddles;

    /// <summary>xyz: the way the wind blows times its strength, zero in a calm; w: its gustiness. See <see cref="Passes.Wind.Pack(WindSettings?, RenderEffects)"/>.</summary>
    public Vector4 Wind;
}

/// <summary>The drawn bake's probes and space, and the flat environment colours.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameBakedBlock
{
    /// <summary>xyz: the drawn bake's first light probe; w: their spacing in metres.</summary>
    public Vector4 BakedProbeMin;

    /// <summary>xyz: how many of the bake's probes run along each axis; w: 1 while they light the baked space, 0 for none.</summary>
    public Vector4 BakedProbeCounts;

    /// <summary>xyz: the near corner of the baked space's box, inside which its probes are read; w: the bake's irradiance atlas's slot in the material table.</summary>
    public Vector4 BakedSpaceMin;

    /// <summary>xyz: the box's far corner; w: the bake's depth atlas's slot in the material table.</summary>
    public Vector4 BakedSpaceMax;

    /// <summary>x: how far the baked space's bake has faded in, from 0 to 1, which scales its lightmaps' and probes' weight; 1 when none is fading in.</summary>
    public Vector4 BakedFade;

    /// <summary>rgb: the irradiance cube's average, the soft light every side gets with Graphics.EnvironmentLight off. See <see cref="Vulkan.EnvironmentMap.Flat"/>.</summary>
    public Vector4 FlatIrradiance;

    /// <summary>rgb: the reflection cube's average at its roughest level, what every reflection sees with Graphics.EnvironmentLight off.</summary>
    public Vector4 FlatReflection;
}

/// <summary>The outlines drawn round things.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameOutlineBlock
{
    /// <summary>rgb: the outlines' colour; w: their thickness in scene pixels, 0 for none.</summary>
    public Vector4 Outline;

    /// <summary>x, y: the distances in metres over which outlines fade out.</summary>
    public Vector4 OutlineFade;
}

/// <summary>The physical sky's air, moon and stars.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameAirBlock
{
    /// <summary>The physical sky's air between the eye and a surface. rgb: how much it dims per metre; w: the aerosol's scattering per metre. See <see cref="Atmosphere.Air"/>.</summary>
    public Vector4 AirExtinction;

    /// <summary>rgb: the sunlight that reaches the air; w: 1 when the air fades distant surfaces, 0 when it doesn't.</summary>
    public Vector4 AirSun;

    /// <summary>xyz: towards the moon; w: 1 when the physical sky shows it. See <see cref="SkyModel.Night"/>.</summary>
    public Vector4 Moon;

    /// <summary>rgb: how bright the moon's lit face is, after the air it comes through.</summary>
    public Vector4 MoonRadiance;

    /// <summary>x: how bright the stars are, 0 for none; y: how far they have turned about the zenith, in radians.</summary>
    public Vector4 Stars;
}

/// <summary>The frame's counts, times and switches.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameCountBlock
{
    public int LightCount;
    public float Time;
    public int FrameNumber;
    public int HistoryValid;

    /// <summary>How many reflection probes the view sees, at the front of the frame's probe list.</summary>
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
}
