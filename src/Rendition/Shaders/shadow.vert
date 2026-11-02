#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "skinning.glsl"
#include "instances.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;
layout(location = 2) in uvec4 inJoints;
layout(location = 3) in vec4 inWeights;

// Must match DrawConstants in Passes/SceneDraws.cs.
layout(push_constant) uniform Draw {
    vec4 tint;
    vec4 ghost; // rgb is the colour of a ghost and a its opacity, or 0 for no ghost.
    int material;
    float swimAmplitude;
    float swimBodyLength;
    float swimPhase;
    int cascade;
    int firstBone;
} draw;

layout(location = 0) out vec2 outUv;

void main() {
    outUv = inUv;
    vec3 posed = (skinMatrix(draw.firstBone, inJoints, inWeights) * vec4(inPosition, 1.0)).xyz;
    vec3 swum = swim(posed, draw.swimAmplitude, draw.swimBodyLength, draw.swimPhase);
    gl_Position = frame.shadowMatrices[draw.cascade] * instanceModel() * vec4(swum, 1.0);
}
