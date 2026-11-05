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
    vec4 shadowFade;         // x, y: distance where shadows start fading and are gone; z: the hand-over band
    vec4 shadowDepthScales;  // depth units per unit across each cascade's map, below 1 when it reaches further sunward
    vec4 clusters;           // x, y: nearest and furthest depth a local light reaches; z: 1 for even slices
    vec4 screen;             // xy: the render targets' size in pixels
    int lightCount;
    float time;              // Seconds, as set by the game.
    int frameNumber;
    int historyValid;        // 1 when the previous frame can be used.
    int probeCount;
    int showEnvironment;     // 1 draws the environment where nothing was drawn.
    int shadowCascades;      // How many of shadowMatrices are in use.
    int localLightCount;     // Point and spot lights in light_data.glsl's lights.
    Light lights[8];
    ReflectionProbe probes[4];
} frame;

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
