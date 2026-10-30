// Shared by every pass. Must match FrameUniforms in Passes/FrameUniforms.cs.
layout(std140, set = 0, binding = 0) uniform Frame {
    mat4 viewProjection;
    mat4 inverseViewProjection;
    vec4 cameraPosition;
    vec4 lightDirection;  // xyz: unit vector pointing towards the light
    vec4 lightRadiance;   // rgb: colour multiplied by intensity
    vec4 ambientRadiance; // rgb
} frame;
