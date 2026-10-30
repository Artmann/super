#version 460
#extension GL_EXT_nonuniform_qualifier : require
#extension GL_GOOGLE_include_directive : require
#include "material.glsl"

layout(location = 0) in vec3 inNormal;
layout(location = 1) in vec4 inTangent;
layout(location = 2) in vec2 inUv;

layout(location = 0) out vec4 outBaseColorMetallic;
layout(location = 1) out vec4 outNormalRoughnessAo;
layout(location = 2) out vec4 outEmissiveClearcoat;

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
    Material material = materials[draw.material];

    vec4 surface = texture(textures[material.surfaceTexture], inUv);
    if (surface.a < material.alphaCutoff) {
        discard;
    }

    vec4 baseColor = texture(textures[material.baseColorTexture], inUv) * material.baseColor;

    // Moves the normal map out of the surface's own tangent space into world space.
    vec3 n = normalize(gl_FrontFacing ? inNormal : -inNormal);
    vec3 t = normalize(inTangent.xyz - n * dot(n, inTangent.xyz));
    vec3 b = cross(n, t) * inTangent.w;
    vec3 mapped = texture(textures[material.normalTexture], inUv).xyz * 2.0 - 1.0;
    vec3 normal = normalize(t * mapped.x + b * mapped.y + n * mapped.z);

    outBaseColorMetallic = vec4(baseColor.rgb, material.metallic * surface.r);
    outNormalRoughnessAo = vec4(octahedralEncode(normal), material.roughness * surface.g, 1.0);

    vec3 emissive = texture(textures[material.emissiveTexture], inUv).rgb * material.emissive.rgb;
    outEmissiveClearcoat = vec4(emissive, material.clearcoat);
}
