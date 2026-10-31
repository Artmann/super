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
    mat4 shadowMatrix;
    mat4 previousViewProjection; // Last frame's, without its anti-aliasing shift.
    vec4 cameraPosition;
    vec4 backgroundRadiance; // rgb: background; a: environment intensity
    vec4 parameters;         // x, y: background fade start, end; z: shadow light angle; w: normal offset
    vec4 grade;              // x: exposure; y: contrast; z: saturation; w: vignette
    vec4 finish;             // x: sharpening; y: grain; zw: this frame's anti-aliasing shift
    vec4 waterAbsorption;    // rgb: light lost per metre; a: 1 when the view is underwater
    vec4 waterShallow;       // rgb: colour of the water looking up; a: height of the surface
    vec4 waterDeep;          // rgb: colour of the water looking down; a: strength of the light shafts
    vec4 waterLight;         // x: strength of the caustics; y: how small they are; z: reach of the shafts
    int lightCount;
    float time;              // Seconds, as set by the game.
    int frameNumber;
    int historyValid;        // 1 when the previous frame can be used.
    int probeCount;
    int showEnvironment;     // 1 draws the environment where nothing was drawn.
    Light lights[8];
    ReflectionProbe probes[4];
} frame;

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
