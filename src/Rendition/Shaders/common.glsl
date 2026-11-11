struct Light {
    vec4 direction; // xyz: unit vector pointing towards the light; w: 1 if it casts the shadow
    vec4 radiance;  // rgb: colour multiplied by intensity
};

struct ReflectionProbe {
    vec4 position; // xyz: where it was captured; w: distance over which it fades out
    vec4 boxMin;   // xyz: the room it describes
    vec4 boxMax;
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
    vec4 occlusion;          // x: how far ambient occlusion looks, in metres; y: its strength, 0 to 1
    vec4 lightProbeMin;      // xyz: the first light probe; w: the probes' spacing in metres
    vec4 lightProbeCounts;   // xyz: probes along each axis; w: 1 when the soft light comes from them
    vec4 skySun;             // xyz: towards the clear sky's sun; w: its turbidity, 0 for the environment's bands
    vec4 sunDisc;            // rgb: how bright the sun's disc is, 0 for none; a: the cosine of its angular radius
    vec4 fog;                // x: density at the fog's height, 0 for none; y: that height; z: falloff per metre; w: sun scatter
    mat4 whiteBalance;       // Linear colour to white-balanced colour, used as mat3 by the tone mapping.
    vec4 curve;              // x: 1 for ACES, 0 for AgX; y, z: the AgX look's power and saturation
    vec4 volumetric;         // x: how far the volumetric fog reaches, 0 for none; y: where its first slice ends; z: anisotropy; w: 1 when last frame's volume can be used
    vec4 puddles;            // x: puddleLevel, the water's height in metres; y: puddleBand, the damp band above it; z: 1 when there is water
    vec4 wind;               // xyz: the way the wind blows times its strength, all zero in a calm; w: its gustiness. See wind.glsl.
    int lightCount;
    float time;              // Seconds, as set by the game.
    int frameNumber;
    int historyValid;        // 1 when the previous frame can be used.
    int probeCount;
    int showEnvironment;     // 1 draws the environment where nothing was drawn.
    int shadowCascades;      // How many of shadowMatrices are in use.
    int localLightCount;     // Point and spot lights in light_data.glsl's lights.
    int off;                 // The RenderEffects left out of this frame: see the OFF_ bits below.
    float mipBias;           // Added to the mip level of material textures: below 0 while anti-aliasing shifts the view.
    int probeCapture;        // 1 while drawing a light probe's faces: alpha holds the distance, negative behind a surface.
    float previousTime;      // Last frame's time, for motion vectors; the same as time when there's no last frame.
    Light lights[8];
    ReflectionProbe probes[4];
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
// Not a RenderEffects bit: set by the renderer while a debug view shows (DebugViewBit in Renderer.cs). Tonemapping
// and the finish pass then hand colours through as they are.
const int OFF_DEBUG_VIEW = 1 << 30;

// The normal target's alpha holds the side a surface faces, 1 or -1. Puddle water writes
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
