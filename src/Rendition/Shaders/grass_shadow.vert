#version 460
#extension GL_GOOGLE_include_directive : require
// One vertex of a blade of grass into a cascade of the sun's shadow map.
#include "common.glsl"
#include "wind.glsl"
#include "grass.glsl"

// Must match GrassConstants in GrassStage.cs.
layout(push_constant) uniform GrassDraw {
    mat4 model;
    ivec4 params;     // x: the cascade
    vec4 densityArea; // unused here
} draw;

void main() {
    GrassBlade blade = blades[gl_InstanceIndex];
    vec3 position = bladePoint(blade, gl_VertexIndex, frame.time).position;
    gl_Position = frame.shadowMatrices[draw.params.x] * vec4(position, 1.0);
}
