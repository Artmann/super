#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;

// Must match DrawConstants in Passes/SceneDraws.cs.
layout(push_constant) uniform Draw {
    mat4 model;
    int material;
    float swimAmplitude;
    float swimBodyLength;
    float swimPhase;
} draw;

layout(location = 0) out vec3 outNormal;
layout(location = 1) out vec4 outTangent;
layout(location = 2) out vec2 outUv;
layout(location = 3) out vec3 outPosition;

void main() {
    mat3 model = mat3(draw.model);
    outNormal = transpose(inverse(model)) * inNormal;
    outTangent = vec4(model * inTangent.xyz, inTangent.w);
    outUv = inUv;
    vec3 swum = swim(inPosition, draw.swimAmplitude, draw.swimBodyLength, draw.swimPhase);
    vec4 position = draw.model * vec4(swum, 1.0);
    outPosition = position.xyz;
    gl_Position = frame.viewProjection * position;
}
