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

// Whether a fading copy keeps the pixel whose noise, from 0 to 1, is threshold. The fade comes from
// instanceFade() in instances.glsl: 0 keeps everything, above 0 keeps the bottom of the noise and
// below 0 the top, so two copies cross-fading never cover the same pixel.
bool fadeKeeps(float fade, float threshold) {
    return fade == 0.0 || (fade > 0.0 ? threshold < fade : threshold >= 1.0 + fade);
}
