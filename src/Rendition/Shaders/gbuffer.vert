#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;

// Must match DrawConstants in Passes/GBufferPass.cs.
layout(push_constant) uniform Draw {
    mat4 model;
    vec4 baseColor;
    float metallic;
    float roughness;
} draw;

layout(location = 0) out vec3 outNormal;

void main() {
    outNormal = transpose(inverse(mat3(draw.model))) * inNormal;
    gl_Position = frame.viewProjection * draw.model * vec4(inPosition, 1.0);
}
