struct Light {
    vec4 direction; // xyz: unit vector pointing towards the light; w: 1 if it casts the shadow
    vec4 radiance;  // rgb: colour multiplied by intensity; w: shadow mask channel, or -1 for none
};

// Shared by every pass. Must match FrameUniforms in Passes/FrameUniforms.cs.
layout(std140, set = 0, binding = 0) uniform Frame {
    mat4 viewProjection;
    mat4 inverseViewProjection;
    mat4 shadowMatrices[4];      // One per cascade, each covering three times the last, fitted to the view.
    mat4 previousViewProjection; // Last frame's, without its anti-aliasing shift.
    mat4 view;                   // World to the camera's own space, where it looks down -z.
    vec4 cameraPosition;
    vec4 backgroundRadiance; // rgb: background; a: environment intensity
    vec4 parameters;         // x, y: background fade start, end; z: shadow light angle; w: normal offset
    vec4 grade;              // x: exposure; y: contrast; z: saturation; w: vignette
    vec4 finish;             // x: sharpening; y: grain; zw: this frame's anti-aliasing shift
    vec4 lift;               // rgb: colour added to the darks; a: how much of the bloom is mixed in
    vec4 gain;               // rgb: what each channel is multiplied by
    vec4 hazeOrigin;         // xyz: the point the haze is measured from; w: 1 to measure across the ground only
    vec4 hazeColor;          // rgb: what things fade into; a: 1 to fade into it rather than the background
    vec4 waterAbsorption;    // rgb: light lost per metre; a: 1 when the view is underwater
    vec4 waterShallow;       // rgb: colour of the water looking up; a: height of the surface
    vec4 waterDeep;          // rgb: colour of the water looking down; a: strength of the light shafts
    vec4 waterLight;         // x: strength of the caustics; y: how small they are; z: reach of the shafts
    vec4 shadowRadii;        // The radius each cascade covers, in metres.
    vec4 shadowFade;         // x, y: distance where shadows start fading and are gone; z: the hand-over band; w: contact shadow length in metres, 0 for none
    vec4 shadowDepthScales;  // depth units per unit across each cascade's map, below 1 when it reaches further sunward
    vec4 clusters;           // x, y: nearest and furthest depth a local light reaches; z: 1 for even slices
    vec4 screen;             // xy: the render targets' size in pixels
    vec4 occlusion;          // x: how far ambient occlusion looks, in metres; y: its strength, 0 to 1; z: the screen bounce's strength, 0 when off; w: how far it looks, in metres
    vec4 lightProbeMin;      // xyz: the first light probe; w: the probes' spacing in metres
    vec4 lightProbeCounts;   // xyz: probes along each axis; w: 1 when the soft light comes from them
    vec4 probeCascadeOrigin[4]; // Per cascade, finest first. xyz: the cell of its minimum corner; w: its spacing in metres
    vec4 probeCascadeCounts; // xyz: probes along each axis of every cascade; w: 1 when the cascades may be read
    vec4 skySun;             // xyz: towards the clear sky's sun; w: its turbidity, 0 for the environment's bands
    vec4 sunDisc;            // rgb: how bright the sun's disc is, 0 for none; a: the cosine of its angular radius
    vec4 fog;                // x: density at the fog's height, 0 for none; y: that height; z: falloff per metre; w: sun scatter
    mat4 colorMatrix;        // The white balance, channel mixer and hue shift on linear colour, used as mat3 by the grade's bake.
    vec4 curve;              // x: 0 for AgX, 1 for ACES, 2 for PBR Neutral; y, z: the AgX look's power and saturation; w: how much of the artist's table shows
    vec4 look;               // x: split toning's balance, -1 to 1; y: 1 when any of the five tones below is set
    vec4 splitShadows;       // rgb: split toning's colour for the shade
    vec4 splitHighlights;    // rgb: split toning's colour for the light
    vec4 toneShadows;        // rgb: each channel's extra brightness in the shadows
    vec4 toneMidtones;       // rgb: each channel's extra brightness in the mid-tones
    vec4 toneHighlights;     // rgb: each channel's extra brightness in the highlights
    vec4 volumetric;         // x: how far the volumetric fog reaches, 0 for none; y: where its first slice ends; z: anisotropy; w: 1 when last frame's volume can be used
    vec4 volumetricGrid;     // x: the volumetric fog's slices in depth; y: scene pixels across a cell
    vec4 puddles;            // x: puddleLevel, the water's height in metres; y: puddleBand, the damp band above it; z: 1 when there is water
    vec4 wind;               // xyz: the way the wind blows times its strength, all zero in a calm; w: its gustiness. See wind.glsl.
    vec4 bakedProbeMin;      // xyz: the drawn bake's first light probe; w: their spacing in metres
    vec4 bakedProbeCounts;   // xyz: the bake's probes along each axis; w: 1 while they light the baked space, banded at its faces, 2 cut off hard (tests only), 0 when not
    vec4 bakedSpaceMin;      // xyz: the baked space's box, where its probes are read; w: the irradiance atlas's slot in the material table
    vec4 bakedSpaceMax;      // xyz: the box's far corner; w: the depth atlas's slot
    vec4 bakedFade;          // x: how far the baked space's bake has faded in, from 0 to 1, for its lightmaps and probes; 1 when none is fading
    vec4 flatIrradiance;     // rgb: the irradiance cube's average, the soft light with Graphics.Lighting.EnvironmentLight off
    vec4 flatReflection;     // rgb: the reflection cube's average at its roughest level, its reflections with it off
    vec4 outline;            // rgb: the outlines' colour; w: their thickness in scene pixels, 0 for none
    vec4 outlineFade;        // x, y: the distances in metres over which outlines fade out
    vec4 airExtinction;      // rgb: how much the physical sky's air dims per metre; w: the aerosol's scattering per metre
    vec4 airSun;             // rgb: the sunlight reaching that air; w: 1 when it fades distant surfaces
    vec4 moon;               // xyz: towards the moon; w: 1 when the physical sky shows it
    vec4 moonRadiance;       // rgb: how bright the moon's lit face is, after the air it comes through
    vec4 stars;              // x: how bright the stars are, 0 for none; y: how far they have turned about the zenith, in radians
    int lightCount;
    float time;              // Seconds, as set by the game.
    int frameNumber;
    int historyValid;        // 1 when the previous frame can be used.
    int probeCount;          // Reflection probes the view sees, in light_data.glsl's reflectionProbes.
    int showEnvironment;     // 1 draws the environment where nothing was drawn.
    int shadowCascades;      // How many of shadowMatrices are in use.
    int localLightCount;     // Point and spot lights in light_data.glsl's lights.
    int off;                 // The RenderEffects left out of this frame: see the OFF_ bits below.
    float mipBias;           // Added to the mip level of material textures: below 0 while anti-aliasing shifts the view.
    int probeLightCount;     // Local lights probe updates read: localLightCount in view, then the rest of the frame's.
    float previousTime;      // Last frame's time, for motion vectors; the same as time when there's no last frame.
    Light lights[8];
    mat4 probeSunMatrix;         // World to the light probes' own sun shadow map, over the largest cascade.
    mat4 reflectionSunMatrix;    // World to the sun shadow map round this frame's reflection probe capture, or zero.
    vec4 screenLens;         // x: colour fringes, 0 to 1; y: speed blur, 0 to 1; zw: where the blur streaks from, in uv
    vec4 screenFlash;        // rgb: the flash's display colour; a: how much of it lights the picture
    vec4 screenFade;         // rgb: the fade's display colour; a: how far the picture fades into it
    vec4 edgeTint;           // rgb: the first edge tint's display colour; a: its strength
    vec4 secondEdgeTint;     // rgb: the second edge tint's display colour; a: its strength
    vec4 screenCanvas;       // x: colour lost; y: how much paper shows; z: a tile's width in pixels, 0 for the texture's; w: its texture slot, -1 for none
    vec4 screenRetro;        // x: a pixel's height as a share of the picture's, 0 for none; y: steps between black and white, 0 for all; z: dither; w: unused
    mat4 cloudRays;          // Clip position to direction from the camera: the unshifted view and projection, inverted, camera at the origin
    mat4 cloudReprojection;  // Relative to this frame's camera to last frame's unshifted clip space
    vec4 cloudShape;         // x: coverage; y: density; z: base height, metres; w: depth, metres
    vec4 cloudWeather;       // xy: the camera in the weather map, metres, wrapped; zw: the same for the cirrus
    vec4 cloudNoise;         // xy: the camera in the shape noise, metres, wrapped; zw: in the detail noise
    vec4 cloudShadow;        // xy: the shadow map's centre less the camera's xz; z: its half width, metres; w: the light casting it, -1 for none
    vec4 cloudLight;         // xyz: towards the light on the clouds; w: 0 none, 1 flat, 2 marched
    vec4 cloudRadiance;      // rgb: that light's radiance; w: how much cirrus
    vec4 cloudFrame;         // x: the pixel of each 2x2 marched this frame; y: 1 with history; z: steps across the layer; w: unused
    vec4 cloudAir;           // rgb: how much the air between the camera and the clouds dims per metre; w: unused
    vec4 cloudLean;          // xy: how far the clouds' tops lean along the wind, metres; zw: the clouds' picture's size in pixels
    mat4 mirrorViewProjection; // world to the planar reflection's mirrored clip space; see MirrorView.cs
    vec4 mirrorPlane;        // x: the plane's height; y: 1 with a mirror picture; z: the camera's height above it, -1 when orthographic; w: mirror pixels per unit tangent, per metre when orthographic
    vec4 mirrorEye;          // xyz: the mirrored camera; w: the mirror picture's last mip
} frame;

// Bits of frame.off. Must match RenderEffects in Types.cs.
const int OFF_AMBIENT_OCCLUSION = 1 << 2;
const int OFF_REFLECTIONS = 1 << 3;
const int OFF_BLOOM = 1 << 5;
const int OFF_MATERIALS = 1 << 6;
const int OFF_MULTISCATTER_SPECULAR = 1 << 9;
const int OFF_WEAR_AND_DIRT = 1 << 10;
const int OFF_AUTO_EXPOSURE = 1 << 12;
const int OFF_DEPTH_OF_FIELD = 1 << 13;
const int OFF_VOLUMETRIC_FOG = 1 << 15;
const int OFF_PUDDLES = 1 << 16;
const int OFF_WIND = 1 << 17;
const int OFF_HEIGHT_MAPS = 1 << 19;
const int OFF_SPECULAR = 1 << 20;
const int OFF_ENVIRONMENT_LIGHT = 1 << 21;
const int OFF_EMISSIVE = 1 << 22;
const int OFF_SUBSURFACE = int(0x80000000u);   // 1 << 31, the last bit the shaders read.
// Not a RenderEffects bit: set by the renderer while a debug view shows (DebugViewBit in Renderer.cs). Tonemapping
// and the finish pass then hand colours through as they are.
const int OFF_DEBUG_VIEW = 1 << 30;
// Not a RenderEffects bit: RenderEffects.Fades is past the bits the shaders read, so the renderer sets this bit,
// DepthFirst's, which only the CPU reads, when it's off (FadesOffBit in Renderer.cs).
const int OFF_FADES = 1 << 29;

// The normal target's alpha holds 1 for a surface. Puddle water writes
// WATER_SIDE instead, so the lighting knows to shade it at the water's surface, not on the
// ground under it, which is all the depth buffer holds.
const float WATER_SIDE = 2.0;

// How much light puddle water reflects straight on: 2%, for water's refractive index of 1.33.
// Must match Puddles.WaterF0 in Passes/Puddles.cs.
const float WATER_F0 = 0.02;

// What every surface is made of when materials are off: light grey, matte clay.
const vec3 PLAIN_COLOR = vec3(0.5);
const float PLAIN_ROUGHNESS = 0.8;

bool isOff(int effect) {
    return (frame.off & effect) != 0;
}

// The sky's soft light round a direction, from the irradiance cube; with Graphics.Lighting.EnvironmentLight off, the
// same flat colour from every side, frame.flatIrradiance, which EnvironmentMap works out once per bake.
vec3 skyIrradiance(samplerCube irradiance, vec3 direction) {
    return isOff(OFF_ENVIRONMENT_LIGHT) ? frame.flatIrradiance.rgb : texture(irradiance, direction).rgb;
}

// The sky seen in a direction by a reflection blurred to lod, from the studio's reflection cube; with
// Graphics.Lighting.EnvironmentLight off, the same flat colour every way, frame.flatReflection.
vec3 skyReflection(samplerCube studio, vec3 direction, float lod) {
    return isOff(OFF_ENVIRONMENT_LIGHT) ? frame.flatReflection.rgb : textureLod(studio, direction, lod).rgb;
}

// How far into the haze a point is: from the camera, or across the ground from the view's haze origin.
float hazeDistance(vec3 position) {
    vec3 away = position - frame.hazeOrigin.xyz;
    if (frame.hazeOrigin.w > 0.5) {
        away.y = 0.0;
    }

    return length(away);
}

// Bends a model side to side in a wave running from head to tail, which is how a fish
// swims. The head faces +z and barely moves; the tail moves the most.
vec3 swim(vec3 position, float amplitude, float bodyLength, float phase) {
    if (amplitude == 0.0) {
        return position;
    }

    float along = clamp(0.5 - position.z / bodyLength, 0.0, 1.0);
    position.x += amplitude * along * along * sin(phase - along * 5.0);
    return position;
}
