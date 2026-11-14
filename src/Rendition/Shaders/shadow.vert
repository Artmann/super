#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "skinning.glsl"
#include "instances.glsl"
#include "wind.glsl"
#include "light_data.glsl"

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inUv;
layout(location = 2) in uvec4 inJoints;
layout(location = 3) in vec4 inWeights;

// Must match DrawConstants in Passes/SceneDraws.cs.
layout(push_constant) uniform Draw {
    vec4 tint;
    vec4 ghost; // rgb is the colour of a ghost and a its opacity, or 0 for no ghost.
    int material;
    int cascade; // a sun's cascade, from 4 on a tile of the local lights' atlas, or -1 for the light probes' sun map
    int firstBone;
    float windBase; // the lowest height of the draw's part, in its own space; see wind.glsl
    float windSway; // the material's share of the wind's lean, 0 to stand still
} draw;

layout(location = 0) out vec2 outUv;

void main() {
    outUv = inUv;
    vec3 posed = (skinMatrix(draw.firstBone, inJoints, inWeights) * vec4(inPosition, 1.0)).xyz;
    vec3 swimming = instanceSwim();
    vec3 swum = swim(posed, swimming.x, swimming.y, swimming.z);
    mat4 light = draw.cascade < 0 ? frame.probeSunMatrix
        : draw.cascade < 4 ? frame.shadowMatrices[draw.cascade] : shadowTiles[draw.cascade - 4].matrix;
    mat4 placed = instanceModel();
    vec3 world = windSwayPlaced((placed * vec4(swum, 1.0)).xyz, placed, posed, draw.windBase, draw.windSway);
    gl_Position = light * vec4(world, 1.0);
}
