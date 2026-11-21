#version 460
#extension GL_GOOGLE_include_directive : require
// The ground grass grows on, seen from straight above into the ground map, its height as its depth, so the scatter
// pass knows where the ground is and how thick the grass on it stands.
#include "common.glsl"
#include "wind.glsl"
#include "grass.glsl"

layout(location = 0) in vec3 inPosition;

// Must match GrassConstants in GrassStage.cs.
layout(push_constant) uniform GrassDraw {
    mat4 model;
    ivec4 params;     // x: the density map's slot, -1 for none; y: its channel; z: the look
    vec4 densityArea; // the density map's corners in the ground's own x and z: xy the first, zw the last
} draw;

layout(location = 0) out vec2 outDensityUv;

void main() {
    vec3 world = (draw.model * vec4(inPosition, 1.0)).xyz;
    vec2 across = (world.xz - grass.capture.xy) / (grass.capture.z * grass.capture.w);
    float depth = (world.y - grass.heights.x) / grass.heights.y;
    outDensityUv = (inPosition.xz - draw.densityArea.xy) / max(draw.densityArea.zw - draw.densityArea.xy, vec2(1e-6));
    gl_Position = vec4(across * 2.0 - 1.0, clamp(depth, 0.0, 1.0), 1.0);
}
