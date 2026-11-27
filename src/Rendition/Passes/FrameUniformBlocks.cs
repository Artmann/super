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

/// <summary>The background the camera sees and the environment's strength, and how things fade into it.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameBackgroundBlock
{
    /// <summary>rgb: background; a: environment intensity.</summary>
    public Vector4 BackgroundRadiance;

    /// <summary>x, y: background fade start and end; z: shadow light angle; w: normal offset.</summary>
    public Vector4 Parameters;
}

/// <summary>How the light clusters cut up the view, and the size of the targets they cover. See LocalLightShadows.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameClusterBlock
{
    /// <summary>
    /// How the view's depth is cut into the clusters' slices. x, y: the nearest and furthest
    /// depth any light reaches; z: 1 when the view is parallel, so the slices are even.
    /// </summary>
    public Vector4 Clusters;

    /// <summary>x, y: the size of the render targets in pixels; z, w: one over them.</summary>
    public Vector4 Screen;
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

/// <summary>
/// What the grade's table is baked from, besides the contrast, saturation, lift and gain in
/// <see cref="FrameGradeBlock"/>. See <see cref="PostGrade.Tone"/> and Shaders/grade_lut.comp.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameToneBlock
{
    /// <summary>
    /// The white balance, channel mixer and hue shift on linear colour, applied before the curve. See
    /// <see cref="PostGrade.ColorMatrix"/>.
    /// </summary>
    public Matrix4x4 ColorMatrix;

    /// <summary>
    /// x: 0 for AgX, 1 for ACES, 2 for Khronos PBR Neutral; y, z: the AgX look's power and saturation; w: how much of
    /// the artist's table shows. See <see cref="PostGrade.Curve"/>.
    /// </summary>
    public Vector4 Curve;

    /// <summary>x: split toning's balance, from -1 to 1; y: 1 when any of the tones below is set, 0 to skip them.</summary>
    public Vector4 Look;

    /// <summary>rgb: split toning's colour for the shade.</summary>
    public Vector4 SplitShadows;

    /// <summary>rgb: split toning's colour for the light.</summary>
    public Vector4 SplitHighlights;

    /// <summary>rgb: each channel's extra brightness in the shadows.</summary>
    public Vector4 Shadows;

    /// <summary>rgb: each channel's extra brightness in the mid-tones.</summary>
    public Vector4 Midtones;

    /// <summary>rgb: each channel's extra brightness in the highlights.</summary>
    public Vector4 Highlights;
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

    /// <summary>rgb: the irradiance cube's average, the soft light every side gets with Graphics.Lighting.EnvironmentLight off. See <see cref="Vulkan.EnvironmentMap.Flat"/>.</summary>
    public Vector4 FlatIrradiance;

    /// <summary>rgb: the reflection cube's average at its roughest level, what every reflection sees with Graphics.Lighting.EnvironmentLight off.</summary>
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

/// <summary>The two extra sun shadow maps' matrices, for the light probes and the reflection probe being captured.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameSunBlock
{
    /// <summary>World to the light probes' own sun shadow map, which covers the largest cascade. See ProbeSunShadow.</summary>
    public Matrix4x4 ProbeSunMatrix;

    /// <summary>
    /// World to the sun shadow map round the box of the reflection probe this frame captures, or zero when it captures
    /// none or no sun casts a shadow. See ReflectionCapturePass.
    /// </summary>
    public Matrix4x4 ReflectionSunMatrix;
}

/// <summary>
/// The screen effects a game drives from play, from <see cref="ScreenEffects"/>. See <see cref="PostGrade.Screen"/>,
/// Shaders/tonemap.frag for the lens and Shaders/finish.frag for the rest.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameScreenBlock
{
    /// <summary>x: the colour fringes, 0 to 1; y: the speed blur, 0 to 1; zw: where the blur streaks from, as a fraction of the screen.</summary>
    public Vector4 ScreenLens;

    /// <summary>rgb: the flash's display colour; a: how much of it lights the picture.</summary>
    public Vector4 ScreenFlash;

    /// <summary>rgb: the fade's display colour; a: how far the picture fades into it.</summary>
    public Vector4 ScreenFade;

    /// <summary>rgb: the first edge tint's display colour; a: its strength.</summary>
    public Vector4 EdgeTint;

    /// <summary>rgb: the second edge tint's display colour; a: its strength.</summary>
    public Vector4 SecondEdgeTint;

    /// <summary>x: how much colour the picture loses; y: how much the paper shows; z: a tile's width in pixels, 0 for the texture's own; w: the paper's texture slot, -1 for none.</summary>
    public Vector4 ScreenCanvas;

    /// <summary>x: a pixel's height as a share of the picture's, 0 for none; y: steps between black and white, 0 for all; z: the dither; w: the soft blur from <see cref="ScreenEffects.Blur"/>, 0 to 1. See <see cref="RetroSettings.Pack"/>.</summary>
    public Vector4 ScreenRetro;
}

/// <summary>
/// The clouds this frame: their shape, where the wind has carried them, the light on them and how the march and its
/// history line up with the view. Written by <see cref="CloudModel.Pack"/>. Must match the cloud fields at the end of
/// Frame in Shaders/common.glsl.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameCloudBlock
{
    /// <summary>A pixel's clip position to its direction from the camera: the unshifted view and projection, inverted, with the camera at the origin.</summary>
    public Matrix4x4 CloudRays;

    /// <summary>A point relative to this frame's camera to last frame's clip space, unshifted, for the clouds' history.</summary>
    public Matrix4x4 CloudReprojection;

    /// <summary>x: coverage, 0 to 1; y: density; z: the base's height in metres; w: the layer's depth in metres.</summary>
    public Vector4 CloudShape;

    /// <summary>xy: where the camera stands in the weather map, in metres, wrapped; zw: the same for the cirrus.</summary>
    public Vector4 CloudWeather;

    /// <summary>xy: where the camera stands in the shape noise, in metres, wrapped; zw: the same for the detail noise.</summary>
    public Vector4 CloudNoise;

    /// <summary>xy: the shadow map's centre less the camera's x and z; z: half its width in metres; w: the light that casts it, -1 for none.</summary>
    public Vector4 CloudShadow;

    /// <summary>xyz: towards the light on the clouds; w: how they're drawn, 0 not at all, 1 as a flat layer, 2 marched.</summary>
    public Vector4 CloudLight;

    /// <summary>rgb: that light's radiance; w: how much cirrus.</summary>
    public Vector4 CloudRadiance;

    /// <summary>x: which pixel of each 2 by 2 the march takes this frame; y: 1 when last frame's clouds can be used; z: the march's steps across the layer.</summary>
    public Vector4 CloudFrame;

    /// <summary>rgb: how much the air between the camera and the clouds dims per metre.</summary>
    public Vector4 CloudAir;

    /// <summary>xy: how far the clouds' tops lean along the wind, in metres; zw: the clouds' picture's width and height in pixels.</summary>
    public Vector4 CloudLean;
}

/// <summary>
/// The planar reflection this frame: the mirrored camera and the plane it mirrors in. Written by
/// <see cref="PlanarStage.Write"/>. Must match the mirror fields at the end of Frame in Shaders/common.glsl.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FrameMirrorBlock
{
    /// <summary>World to the mirror's clip space, with the plane as its near plane. See <see cref="MirrorView"/>.</summary>
    public Matrix4x4 MirrorViewProjection;

    /// <summary>x: the plane's height; y: 1 when the frame has a mirror picture; z: how high the camera is above the plane, or -1 for an orthographic camera; w: the mirror picture's pixels across one unit of tangent, up and down, or across a metre when orthographic.</summary>
    public Vector4 MirrorPlane;

    /// <summary>xyz: where the mirrored camera stands; w: the mirror picture's last mip.</summary>
    public Vector4 MirrorEye;
}
