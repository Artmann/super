#version 460
#extension GL_GOOGLE_include_directive : require
// One vertex of a blade of grass into a cascade of the sun's shadow map, drawn once for each blade at one detail.
#include "common.glsl"
#include "wind.glsl"
#include "grass.glsl"

// Must match GrassConstants in GrassStage.cs.
layout(push_constant) uniform GrassDraw {
    mat4 model;
    ivec4 params;     // x: the cascade; w: the detail the blades are drawn at
    vec4 densityArea; // unused here
} draw;

void main() {
    int detail = draw.params.w;
    GrassBlade blade = blades[grass.regions[detail] + gl_InstanceIndex];
    vec3 position = bladePoint(blade, gl_VertexIndex, detail, frame.time).position;
    gl_Position = frame.shadowMatrices[draw.params.x] * vec4(position, 1.0);
}
