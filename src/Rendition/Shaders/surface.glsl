// What a surface shader can use. Write main() to fill in a Surface and pass it to
// writeSurface(). Lighting, shadows and post-processing then apply as usual.
#extension GL_EXT_nonuniform_qualifier : require
#include "common.glsl"
#include "material.glsl"

layout(location = 0) in vec3 inNormal;
layout(location = 1) in vec4 inTangent;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec3 inPosition;

layout(location = 0) out vec4 outBaseColorMetallic;
layout(location = 1) out vec4 outNormalRoughnessAo;
layout(location = 2) out vec4 outEmissiveClearcoat;

struct Surface {
    vec3 baseColor;
    float metallic;
    float roughness;
    vec3 normal; // World space, unit length.
    vec3 emissive;
    float clearcoat;
};

// Maps a unit vector onto the [-1, 1] square so a normal fits in two channels.
vec2 octahedralEncode(vec3 n) {
    n /= abs(n.x) + abs(n.y) + abs(n.z);
    if (n.z < 0.0) {
        vec2 signs = vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
        n.xy = (1.0 - abs(n.yx)) * signs;
    }
    return n.xy;
}

// The surface as the material's constants and textures describe it, with cut-out
// pixels discarded. Custom shaders can start from this and change what they need.
Surface materialSurface() {
    Material material = materials[draw.material];

    vec4 mask = texture(textures[material.surfaceTexture], inUv);
    if (mask.a < material.alphaCutoff) {
        discard;
    }

    // Moves the normal map out of the surface's own tangent space into world space.
    vec3 n = normalize(gl_FrontFacing ? inNormal : -inNormal);
    vec3 t = normalize(inTangent.xyz - n * dot(n, inTangent.xyz));
    vec3 b = cross(n, t) * inTangent.w;
    vec3 mapped = texture(textures[material.normalTexture], inUv).xyz * 2.0 - 1.0;

    Surface surface;
    surface.baseColor = (texture(textures[material.baseColorTexture], inUv) * material.baseColor).rgb;
    surface.metallic = material.metallic * mask.r;
    surface.roughness = material.roughness * mask.g;
    surface.normal = normalize(t * mapped.x + b * mapped.y + n * mapped.z);
    surface.emissive = texture(textures[material.emissiveTexture], inUv).rgb * material.emissive.rgb;
    surface.clearcoat = material.clearcoat;
    return surface;
}

void writeSurface(Surface surface) {
    outBaseColorMetallic = vec4(surface.baseColor, surface.metallic);
    outNormalRoughnessAo = vec4(octahedralEncode(surface.normal), surface.roughness, 1.0);
    outEmissiveClearcoat = vec4(surface.emissive, surface.clearcoat);
}
