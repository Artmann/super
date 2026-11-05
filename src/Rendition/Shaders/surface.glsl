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

// How much sunlight shines through foliage from behind, 0 to 1.
const float FOLIAGE_TRANSLUCENCY = 0.6;

// The surface as the material's constants and textures describe it, with cut-out
// pixels discarded. Custom shaders can start from this and change what they need.
Surface materialSurface() {
    Material material = materials[draw.material];

    // Opacity can come from either texture. Asset packs and glTF keep it in the colour
    // texture, and both read white where a material has no texture.
    vec4 baseColor = texture(textures[material.baseColorTexture], inUv) * material.baseColor;
    vec4 mask = texture(textures[material.surfaceTexture], inUv);
    if (baseColor.a * mask.a < material.alphaCutoff) {
        discard;
    }

    // Moves the normal map out of the surface's own tangent space into world space.
    // Foliage keeps the normal its mesh gives, which points out of the whole crown.
    bool foliage = (material.flags & MATERIAL_FOLIAGE) != 0u;
    vec3 n = normalize(gl_FrontFacing || foliage ? inNormal : -inNormal);
    vec3 t = normalize(inTangent.xyz - n * dot(n, inTangent.xyz));
    vec3 b = cross(n, t) * inTangent.w;
    vec3 mapped = texture(textures[material.normalTexture], inUv).xyz * 2.0 - 1.0;

    Surface surface;
    surface.baseColor = baseColor.rgb;
    surface.metallic = material.metallic * mask.r;
    surface.roughness = material.roughness * mask.g;
    surface.normal = normalize(t * mapped.x + b * mapped.y + n * mapped.z);
    surface.emissive = texture(textures[material.emissiveTexture], inUv).rgb * material.emissive.rgb;
    surface.clearcoat = material.clearcoat;
    return surface;
}

// The material's shader texture in the given slot, 0 to 3. Unset slots read white.
vec4 shaderTexture(int slot, vec2 uv) {
    return texture(textures[materials[draw.material].shaderTextures[slot]], uv);
}

// One layer of the material's shader texture array in the given slot, 0 to 3. Layers
// blend when the layer number is fractional. Unset slots read white.
vec4 shaderLayer(int slot, vec2 uv, float layer) {
    return texture(textureArrays[materials[draw.material].shaderArrays[slot]], vec3(uv, layer));
}

// Applies the draw's tint, so every surface shader gets it. Foliage has no clearcoat, so
// its translucency travels in the same channel as a negative number, and a subsurface
// material puts its index there, plus SUBSURFACE_MARK, so the lighting and blur find it.
// A ghost keeps only the surface's shape: one matte colour that glows a little,
// whatever the material.
void writeSurface(Surface surface) {
    if (draw.ghost.a > 0.0) {
        vec3 color = clamp(draw.ghost.rgb, 0.0, 1.0);
        outBaseColorMetallic = vec4(color, 0.0);
        outNormalRoughnessAo = vec4(octahedralEncode(surface.normal), 0.7, 1.0);
        outEmissiveClearcoat = vec4(color * GHOST_GLOW, 0.0);
        return;
    }

    Material material = materials[draw.material];
    bool foliage = (material.flags & MATERIAL_FOLIAGE) != 0u;
    float channel = foliage ? -FOLIAGE_TRANSLUCENCY : surface.clearcoat;
    if (material.subsurface.w > 0.0) {
        channel = SUBSURFACE_MARK + float(draw.material);
    }
    outBaseColorMetallic = vec4(surface.baseColor * draw.tint.rgb, surface.metallic);
    outNormalRoughnessAo = vec4(octahedralEncode(surface.normal), surface.roughness, 1.0);
    outEmissiveClearcoat = vec4(surface.emissive, channel);
}
