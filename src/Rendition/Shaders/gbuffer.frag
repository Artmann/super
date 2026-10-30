#version 460

layout(location = 0) in vec3 inNormal;

layout(push_constant) uniform Draw {
    mat4 model;
    vec4 baseColor;
    float metallic;
    float roughness;
} draw;

layout(location = 0) out vec4 outBaseColorMetallic;
layout(location = 1) out vec4 outNormalRoughnessAo;

// Maps a unit vector onto the [-1, 1] square so a normal fits in two channels.
vec2 octahedralEncode(vec3 n) {
    n /= abs(n.x) + abs(n.y) + abs(n.z);
    if (n.z < 0.0) {
        vec2 signs = vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
        n.xy = (1.0 - abs(n.yx)) * signs;
    }
    return n.xy;
}

void main() {
    outBaseColorMetallic = vec4(draw.baseColor.rgb, draw.metallic);
    outNormalRoughnessAo = vec4(octahedralEncode(normalize(inNormal)), draw.roughness, 1.0);
}
