#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : require
// How thick the grass stands on the ground drawn into the ground map, and which look it has.

layout(set = 1, binding = 0) uniform sampler2D textures[];

// Must match GrassConstants in GrassStage.cs.
layout(push_constant) uniform GrassDraw {
    mat4 model;
    ivec4 params;     // x: the density map's slot, -1 for none; y: its channel; z: the look
    vec4 densityArea; // the density map's corners in the ground's own x and z: xy the first, zw the last
} draw;

layout(location = 0) in vec2 inDensityUv;
layout(location = 0) out vec4 outInfo;

void main() {
    float density = 1.0;
    if (draw.params.x >= 0) {
        // Outside the map is bare, so a map smaller than its ground grows grass on only its part.
        bool inside = all(greaterThanEqual(inDensityUv, vec2(0.0))) && all(lessThanEqual(inDensityUv, vec2(1.0)));
        density = inside ? texture(textures[nonuniformEXT(draw.params.x)], inDensityUv)[draw.params.y] : 0.0;
    }

    outInfo = vec4(clamp(density, 0.0, 1.0), float(draw.params.z) / 255.0, 0.0, 1.0);
}
