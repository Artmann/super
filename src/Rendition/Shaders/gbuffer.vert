#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "skinning.glsl"
#include "instances.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 2) in vec4 inTangent;
layout(location = 3) in vec2 inUv;
layout(location = 4) in uvec4 inJoints;
layout(location = 5) in vec4 inWeights;

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

layout(location = 0) out vec3 outNormal;
layout(location = 1) out vec4 outTangent;
layout(location = 2) out vec2 outUv;
layout(location = 3) out vec3 outPosition;

void main() {
    mat4 skinned = skinMatrix(draw.firstBone, inJoints, inWeights);
    mat4 placed = instanceModel();
    mat3 model = mat3(placed) * mat3(skinned);
    // The cofactor matrix is the inverse transpose times the determinant. The size doesn't
    // matter, because the fragment shader normalizes, but the sign keeps mirrored models
    // right. This avoids inverting a matrix for every vertex.
    mat3 cofactor = mat3(cross(model[1], model[2]), cross(model[2], model[0]), cross(model[0], model[1]));
    outNormal = cofactor * inNormal * sign(dot(model[0], cofactor[0]));
    outTangent = vec4(model * inTangent.xyz, inTangent.w);
    outUv = inUv;
    vec3 posed = (skinned * vec4(inPosition, 1.0)).xyz;
    vec3 swum = swim(posed, draw.swimAmplitude, draw.swimBodyLength, draw.swimPhase);
    vec4 position = placed * vec4(swum, 1.0);
    outPosition = position.xyz;
    gl_Position = frame.viewProjection * position;
}
