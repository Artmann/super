// What a surface shader can use. Write main() to fill in a Surface and pass it to
// writeSurface(). Lighting, shadows and post-processing then apply as usual.
#extension GL_EXT_nonuniform_qualifier : require
#include "common.glsl"
#include "material.glsl"
#include "sampling.glsl"

layout(location = 0) in vec3 inNormal;
layout(location = 1) in vec4 inTangent;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec3 inPosition;
layout(location = 4) in vec2 inUv2;

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

// What a material with world variation is multiplied by in its darkest and lightest
// patches. The darker end is a little warmer, as grime is.
const vec3 WORLD_VARIATION_DARK = vec3(0.8, 0.77, 0.74);
const vec3 WORLD_VARIATION_LIGHT = vec3(1.1, 1.1, 1.1);

// Smooth noise from 0 to 1 over world space, one bump about every unit.
float worldNoise(vec3 p) {
    vec3 cell = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float corners[8];
    for (int i = 0; i < 8; i++) {
        vec3 corner = cell + vec3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        corners[i] = fract(sin(dot(corner, vec3(127.1, 311.7, 74.7))) * 43758.5453);
    }

    float x0 = mix(mix(corners[0], corners[1], f.x), mix(corners[2], corners[3], f.x), f.y);
    float x1 = mix(mix(corners[4], corners[5], f.x), mix(corners[6], corners[7], f.x), f.y);
    return mix(x0, x1, f.z);
}

// Broad patches of a few metres with smaller ones over them, laid by where the surface is in
// the world rather than by its uv, so they never repeat with the texture.
float worldVariation(vec3 position) {
    float broad = worldNoise(position * 0.35);
    float smaller = worldNoise(position * 1.3 + 17.0);
    return smoothstep(0.15, 0.85, broad * 0.65 + smaller * 0.35);
}

// Wear starts where the mask's curvature passes the first of these and is whole at the second.
// 0.5 is flat, so a little rounding stays clean.
const float WEAR_FROM = 0.55;
const float WEAR_FULL = 0.8;

// Dirt gathers below this height above the mesh's base, in metres, more the lower it is.
const float DIRT_HEIGHT = 0.3;

// Lays the material's wear and dirt over a colour and roughness, by the masks baked from its
// mesh and read through the second uv. Wear goes on convex edges, and dirt in hollows the sky
// can't reach and low down near the base, but not over wear, which rubs it off.
void applyLayers(Material material, inout vec3 color, inout float roughness) {
    if (material.wear.a <= 0.0 && material.dirt.a <= 0.0) {
        return;
    }

    vec4 mask = texture(textures[material.maskTexture], inUv2, frame.mipBias);
    float wear = smoothstep(WEAR_FROM, WEAR_FULL, mask.r) * material.wear.a;
    float height = mask.b * material.maskHeight;
    float dirt = clamp(max(1.0 - mask.g, 1.0 - height / DIRT_HEIGHT), 0.0, 1.0) * material.dirt.a * (1.0 - wear);

    color = mix(color, material.wear.rgb, wear);
    if (material.wearRoughness >= 0.0) {
        roughness = mix(roughness, material.wearRoughness, wear);
    }

    color = mix(color, material.dirt.rgb, dirt);
    if (material.dirtRoughness >= 0.0) {
        roughness = mix(roughness, material.dirtRoughness, dirt);
    }
}

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

// A tangent-space normal from a normal map's red and green. The third axis is worked out
// from them rather than read, since compressed normal maps keep only those two.
vec3 normalFrom(vec4 texel) {
    vec2 xy = texel.xy * 2.0 - 1.0;
    return vec3(xy, sqrt(max(1.0 - dot(xy, xy), 0.0)));
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

    // A sharper mip while anti-aliasing shifts the view (frame.mipBias): one frame alone
    // would shimmer, but the frames it blends together settle.
    float sharper = exp2(frame.mipBias);
    vec2 uvX = dFdx(inUv) * sharper;
    vec2 uvY = dFdy(inUv) * sharper;

    // Opacity can come from either texture. Asset packs and glTF keep it in the colour
    // texture, and both read white where a material has no texture.
    vec4 baseColor = textureGrad(textures[material.baseColorTexture], uv, uvX, uvY) * material.baseColor;
    vec4 mask = textureGrad(textures[material.surfaceTexture], uv, uvX, uvY);
    if (baseColor.a * mask.a < material.alphaCutoff) {
        discard;
    }

    // A dithered material keeps a share of its pixels as large as its opacity, a different
    // share each frame, and anti-aliasing blends them with what's behind into a soft fade.
    if ((material.flags & MATERIAL_DITHERED) != 0u) {
        vec2 pixel = gl_FragCoord.xy + 5.588238 * float(frame.frameNumber % 64);
        float threshold = fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
        if (baseColor.a * mask.a <= threshold) {
            discard;
        }
    }

    vec3 mapped = normalize(normalFrom(textureGrad(textures[material.normalTexture], uv, uvX, uvY)));
    if (material.detailScale > 0.0) {
        float strength = (material.flags & MATERIAL_DETAIL_MASK) != 0u ? mask.b : 1.0;
        vec2 detailUv = uv * material.detailScale;
        vec2 detailX = uvX * material.detailScale;
        vec2 detailY = uvY * material.detailScale;
        vec3 grain = textureGrad(textures[material.detailColorTexture], detailUv, detailX, detailY).rgb / DETAIL_NEUTRAL;
        baseColor.rgb *= mix(vec3(1.0), grain, strength);
        vec3 detail = normalFrom(textureGrad(textures[material.detailNormalTexture], detailUv, detailX, detailY));
        mapped = blendNormals(mapped, normalize(mix(vec3(0.0, 0.0, 1.0), detail, strength)));
    }

    // Moves the normal map out of the surface's own tangent space into world space.
    mat3 basis = surfaceFrame();

    float roughness = material.roughness * mask.g;
    if ((material.flags & MATERIAL_WORLD_VARIATION) != 0u) {
        float variation = worldVariation(inPosition);
        baseColor.rgb *= mix(WORLD_VARIATION_DARK, WORLD_VARIATION_LIGHT, variation);
        roughness = clamp(roughness * mix(1.08, 0.94, variation), 0.0, 1.0);
    }

    applyLayers(material, baseColor.rgb, roughness);

    Surface surface;
    surface.baseColor = baseColor.rgb;
    surface.metallic = material.metallic * mask.r;
    surface.roughness = roughness;
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
    return texture(textures[materials[draw.material].shaderTextures[slot]], uv, frame.mipBias);
}

// One layer of the material's shader texture array in the given slot, 0 to 3. Layers
// blend when the layer number is fractional. Unset slots read white.
vec4 shaderLayer(int slot, vec2 uv, float layer) {
    return texture(textureArrays[materials[draw.material].shaderArrays[slot]], vec3(uv, layer), frame.mipBias);
}

// Applies the draw's tint, so every surface shader gets it. Foliage has no clearcoat, so
// its translucency travels in the same channel as a negative number, and a subsurface
// material puts its index there, plus SUBSURFACE_MARK, so the lighting and blur find it.
// A ghost keeps only the surface's shape: one matte colour that glows a little,
// whatever the material.
// The normal's alpha is -1 on the back of a one-sided surface. Only a light probe's capture
// draws those, to learn which of its probes sit inside a wall; the lighting reads its size.
void writeSurface(Surface surface) {
    Material material = materials[draw.material];
    bool back = !gl_FrontFacing && (material.flags & (MATERIAL_TWO_SIDED | MATERIAL_FOLIAGE)) == 0u;
    float side = back ? -1.0 : 1.0;

    if (draw.ghost.a > 0.0) {
        vec3 color = clamp(draw.ghost.rgb, 0.0, 1.0);
        outBaseColorMetallic = vec4(color, 0.0);
        outNormalRoughnessAo = vec4(octahedralEncode(surface.normal), 0.7, side);
        outEmissiveClearcoat = vec4(color * GHOST_GLOW, 0.0);
        return;
    }

    bool foliage = (material.flags & MATERIAL_FOLIAGE) != 0u;

    // With materials off, every surface is the same plain clay, shaped only by its mesh: no
    // colour, tint, normal map, shine, coat or subsurface. Cut-outs still cut and glows still glow.
    if (isOff(OFF_MATERIALS)) {
        outBaseColorMetallic = vec4(PLAIN_COLOR, 0.0);
        outNormalRoughnessAo = vec4(octahedralEncode(surfaceFrame()[2]), PLAIN_ROUGHNESS, side);
        outEmissiveClearcoat = vec4(surface.emissive, foliage ? -FOLIAGE_TRANSLUCENCY : 0.0);
        return;
    }

    float channel = foliage ? -FOLIAGE_TRANSLUCENCY : surface.clearcoat;
    if (material.subsurface.w > 0.0) {
        channel = SUBSURFACE_MARK + float(draw.material);
    }
    outBaseColorMetallic = vec4(surface.baseColor * draw.tint.rgb, surface.metallic);
    outNormalRoughnessAo = vec4(octahedralEncode(surface.normal), surface.roughness, side);
    outEmissiveClearcoat = vec4(surface.emissive, channel);
}
