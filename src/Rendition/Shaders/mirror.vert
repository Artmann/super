#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "skinning.glsl"
#include "instances.glsl"
#include "wind.glsl"

// Draws the scene for the planar reflection, from the camera mirrored in the plane, as frame.mirrorViewProjection
// places it: skinned, swimming and swaying as the G-buffer draws it, so a reflection moves with what it reflects.
// See PlanarStage.cs.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 3) in vec2 inUv;
layout(location = 4) in uvec4 inJoints;
layout(location = 5) in vec4 inWeights;
// The tangent (2) and second UV (6) are left out: nothing here bends a normal or reads a baked mask.

// Must match DrawConstants in Passes/SceneDraws.cs.
layout(push_constant) uniform Draw {
    vec4 tint;
    vec4 ghost;
    int material;
    int cascade;
    int firstBone;
    float windBase;
    float windSway;
} draw;

layout(location = 0) out vec3 outNormal;
layout(location = 1) out vec2 outUv;
layout(location = 2) out vec3 outPosition;

void main() {
    mat4 skinned = skinMatrix(draw.firstBone, inJoints, inWeights);
    mat4 placed = instanceModel();
    mat3 model = mat3(placed) * mat3(skinned);
    // The cofactor turns normals as the inverse transpose would, its sign keeping mirrored models right.
    mat3 cofactor = mat3(cross(model[1], model[2]), cross(model[2], model[0]), cross(model[0], model[1]));
    outNormal = cofactor * inNormal * sign(dot(model[0], cofactor[0]));
    outUv = inUv;
    vec3 posed = (skinned * vec4(inPosition, 1.0)).xyz;
    vec3 swimming = instanceSwim();
    vec3 swum = swim(posed, swimming.x, swimming.y, swimming.z);
    vec3 world = windSwayPlaced((placed * vec4(swum, 1.0)).xyz, placed, posed, draw.windBase, draw.windSway);
    outPosition = world;
    gl_Position = frame.mirrorViewProjection * vec4(world, 1.0);
}
