// Must match DrawConstants in Passes/SceneDraws.cs.
layout(push_constant) uniform Draw {
    vec4 tint; // rgb multiplies the material's colour, for this draw only. w: 1 when glass sees the scene through the copy.
    vec4 ghost; // rgb is the colour of a ghost and a its opacity, or 0 for no ghost.
    int material;
    int cascade;
    int firstBone;
    float windBase; // the lowest height of the draw's part, in its own space; see wind.glsl
    float windSway; // the material's share of the wind's lean, 0 to stand still
} draw;

// How brightly a ghost glows, so its colour reads in shadow and on the dark side.
const float GHOST_GLOW = 0.35;

#include "material_data.glsl"

layout(set = 1, binding = 0) uniform sampler2D textures[];
layout(set = 1, binding = 2) uniform sampler2DArray textureArrays[];
layout(std430, set = 1, binding = 1) readonly buffer Materials {
    Material materials[];
};
