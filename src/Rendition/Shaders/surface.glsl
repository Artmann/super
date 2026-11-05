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

// Parallax fades out between these distances from the camera, in metres, and is off past
// the far one. Grooves a few centimetres deep are too small to see that far away.
const float PARALLAX_FADE_START = 15.0;
const float PARALLAX_FADE_END = 25.0;

// How many steps the view ray takes into the height map: few when looking straight at
// the surface, where it hardly travels, and more at grazing angles, where it travels far.
const int PARALLAX_MIN_STEPS = 8;
const int PARALLAX_MAX_STEPS = 32;

// The steepest the ray is followed, as the cosine of its angle to the surface. Flatter
// views would slide the texture without limit.
const float PARALLAX_FLATTEST = 0.1;

// The value a detail colour texture has where it changes nothing.
const float DETAIL_NEUTRAL = 128.0 / 255.0;

// The surface's frame in world space: tangent, bitangent and normal. Back faces flip the
// normal, except foliage, which keeps the normal its mesh gives, out of the whole crown.
mat3 surfaceFrame() {
    bool foliage = (materials[draw.material].flags & MATERIAL_FOLIAGE) != 0u;
    vec3 n = normalize(gl_FrontFacing || foliage ? inNormal : -inNormal);
    vec3 t = normalize(inTangent.xyz - n * dot(n, inTangent.xyz));
    vec3 b = cross(n, t) * inTangent.w;
    return mat3(t, b, n);
}

// Texture units per metre on this pixel, from how fast the uv and the position change
// across the screen. Exact for a mapping that stretches both directions the same.
float uvPerMetre(vec2 uvX, vec2 uvY) {
    vec3 positionX = dFdx(inPosition);
    vec3 positionY = dFdy(inPosition);
    float metres = dot(positionX, positionX) + dot(positionY, positionY);
    return metres > 0.0 ? sqrt((dot(uvX, uvX) + dot(uvY, uvY)) / metres) : 0.0;
}

// Follows the view ray down into a height map (red, white at the top) until it meets
// the surface, and returns the uv it meets it at. depth is how far below the top black
// lies, in metres. uvX and uvY are the uv's screen derivatives, which keep the mip level
// steady at the steep edges of grooves.
vec2 parallaxUvIn(uint heightTexture, vec2 uv, float depth, mat3 basis, vec2 uvX, vec2 uvY) {
    // Screen derivatives are only defined before any pixel of the quad leaves early.
    float density = uvPerMetre(uvX, uvY);
    vec3 toEye = frame.cameraPosition.xyz - inPosition;
    float distance = length(toEye);
    float fade = 1.0 - smoothstep(PARALLAX_FADE_START, PARALLAX_FADE_END, distance);
    vec3 view = transpose(basis) * (toEye / max(distance, 1e-6));
    if (depth <= 0.0 || fade <= 0.0 || view.z <= 0.0) {
        return uv;
    }

    // How far the uv slides from the top of the height map to its deepest point.
    vec2 slide = -view.xy / max(view.z, PARALLAX_FLATTEST) * depth * fade * density;
    int steps = int(mix(float(PARALLAX_MAX_STEPS), float(PARALLAX_MIN_STEPS), view.z) * fade + 0.5);
    steps = max(steps, PARALLAX_MIN_STEPS / 2);
    float layer = 1.0 / float(steps);

    float ray = 1.0;
    vec2 at = uv;
    float height = textureGrad(textures[heightTexture], at, uvX, uvY).r;
    float previousRay = ray;
    float previousHeight = height;
    for (int i = 0; i < steps && ray > height; i++) {
        previousRay = ray;
        previousHeight = height;
        ray -= layer;
        at += slide * layer;
        height = textureGrad(textures[heightTexture], at, uvX, uvY).r;
    }

    // Where between the last two steps the ray crossed the surface, so the result moves
    // smoothly as the view does instead of jumping a step at a time.
    float below = height - ray;
    float above = previousRay - previousHeight;
    float back = below + above > 0.0 ? below / (below + above) : 0.0;
    return at - slide * layer * back;
}

// Parallax for a surface shader: the uv shifted by the height map in the shader texture
// in the given slot (red, white at the top). depth is how far below the top black lies,
// in metres. Sample the other textures at the uv it returns.
vec2 parallaxUv(int slot, vec2 uv, float depth) {
    return parallaxUvIn(materials[draw.material].shaderTextures[slot], uv, depth, surfaceFrame(), dFdx(uv), dFdy(uv));
}

// The material's own uv for this pixel, shifted by its height texture when it has one.
vec2 materialUv() {
    Material material = materials[draw.material];
    return parallaxUvIn(material.heightTexture, inUv, material.heightDepth, surfaceFrame(), dFdx(inUv), dFdy(inUv));
}

// Lays a detail normal over a base one, both in tangent space, so the detail follows the
// base's slopes. This is reoriented normal mapping.
vec3 blendNormals(vec3 base, vec3 detail) {
    vec3 t = base + vec3(0.0, 0.0, 1.0);
    vec3 u = detail * vec3(-1.0, -1.0, 1.0);
    return normalize(t * dot(t, u) / t.z - u);
}

// The surface as the material's constants and textures describe it at this uv, with
// cut-out pixels discarded. Textures pick their mip level from inUv, so pass inUv or a uv
// shifted from it, such as parallaxUv's.
Surface materialSurface(vec2 uv) {
    Material material = materials[draw.material];
    vec2 uvX = dFdx(inUv);
    vec2 uvY = dFdy(inUv);

    // Opacity can come from either texture. Asset packs and glTF keep it in the colour
    // texture, and both read white where a material has no texture.
    vec4 baseColor = textureGrad(textures[material.baseColorTexture], uv, uvX, uvY) * material.baseColor;
    vec4 mask = textureGrad(textures[material.surfaceTexture], uv, uvX, uvY);
    if (baseColor.a * mask.a < material.alphaCutoff) {
        discard;
    }

    vec3 mapped = normalize(textureGrad(textures[material.normalTexture], uv, uvX, uvY).xyz * 2.0 - 1.0);
    if (material.detailScale > 0.0) {
        float strength = (material.flags & MATERIAL_DETAIL_MASK) != 0u ? mask.b : 1.0;
        vec2 detailUv = uv * material.detailScale;
        vec2 detailX = uvX * material.detailScale;
        vec2 detailY = uvY * material.detailScale;
        vec3 grain = textureGrad(textures[material.detailColorTexture], detailUv, detailX, detailY).rgb / DETAIL_NEUTRAL;
        baseColor.rgb *= mix(vec3(1.0), grain, strength);
        vec3 detail = textureGrad(textures[material.detailNormalTexture], detailUv, detailX, detailY).xyz * 2.0 - 1.0;
        mapped = blendNormals(mapped, normalize(mix(vec3(0.0, 0.0, 1.0), detail, strength)));
    }

    // Moves the normal map out of the surface's own tangent space into world space.
    mat3 basis = surfaceFrame();

    Surface surface;
    surface.baseColor = baseColor.rgb;
    surface.metallic = material.metallic * mask.r;
    surface.roughness = material.roughness * mask.g;
    surface.normal = normalize(basis * mapped);
    surface.emissive = textureGrad(textures[material.emissiveTexture], uv, uvX, uvY).rgb * material.emissive.rgb;
    surface.clearcoat = material.clearcoat;
    return surface;
}

// The surface as the material's constants and textures describe it, shifted by its
// height texture, with cut-out pixels discarded. Custom shaders can start from this and
// change what they need.
Surface materialSurface() {
    return materialSurface(materialUv());
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
