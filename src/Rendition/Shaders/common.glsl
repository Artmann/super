struct Light {
    vec4 direction; // xyz: unit vector pointing towards the light; w: 1 if it casts the shadow
    vec4 radiance;  // rgb: colour multiplied by intensity
};

// Shared by every pass. Must match FrameUniforms in Passes/FrameUniforms.cs.
layout(std140, set = 0, binding = 0) uniform Frame {
    mat4 viewProjection;
    mat4 inverseViewProjection;
    mat4 shadowMatrix;
    vec4 cameraPosition;
    vec4 ambientRadiance;    // rgb
    vec4 backgroundRadiance; // rgb
    vec4 parameters;         // x, y: background fade start, end; z: shadow light angle; w: normal offset
    int lightCount;
    Light lights[8];
} frame;
