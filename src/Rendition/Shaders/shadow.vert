#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;

// Must match DrawConstants in Passes/SceneDraws.cs.
layout(push_constant) uniform Draw {
    mat4 model;
    int material;
    float swimAmplitude;
    float swimBodyLength;
    float swimPhase;
    int cascade;
} draw;

layout(location = 0) out vec2 outUv;

void main() {
    outUv = inUv;
    vec3 swum = swim(inPosition, draw.swimAmplitude, draw.swimBodyLength, draw.swimPhase);
    gl_Position = frame.shadowMatrices[draw.cascade] * draw.model * vec4(swum, 1.0);
}
