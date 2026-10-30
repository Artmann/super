#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;

// Must match DrawConstants in Passes/GBufferPass.cs.
layout(push_constant) uniform Draw {
    mat4 model;
    int material;
} draw;

layout(location = 0) out vec3 outNormal;
layout(location = 1) out vec4 outTangent;
layout(location = 2) out vec2 outUv;

void main() {
    mat3 model = mat3(draw.model);
    outNormal = transpose(inverse(model)) * inNormal;
    outTangent = vec4(model * inTangent.xyz, inTangent.w);
    outUv = inUv;
    gl_Position = frame.viewProjection * draw.model * vec4(inPosition, 1.0);
}
