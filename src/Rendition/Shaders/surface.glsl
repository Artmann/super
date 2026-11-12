// What a surface shader can use. Write main() to fill in a Surface and pass it to
// writeSurface(). Lighting, shadows and post-processing then apply as usual.
#extension GL_EXT_nonuniform_qualifier : require
#include "common.glsl"
#include "material.glsl"
#include "sampling.glsl"
#include "chunks.glsl"

layout(location = 0) in vec3 inNormal;
layout(location = 1) in vec4 inTangent;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec3 inPosition;
layout(location = 4) in vec2 inUv2;
layout(location = 5) in vec3 inClipNow;    // clip x, y and w, without the anti-aliasing shift; see gbuffer.vert
layout(location = 6) in vec3 inClipBefore; // the same, last frame
layout(location = 7) in vec2 inLightmapUv; // where the pixel is on its lightmap page; see gbuffer.vert
// The page's irradiance, direction and mask textures, -1 for none, and in w the copy's placed chunk in chunks.glsl, -1
// for none.
layout(location = 8) flat in ivec4 inLightmap;
layout(location = 9) in vec3 inChunkPosition; // where the pixel is in that chunk's own space

layout(location = 0) out vec4 outBaseColorMetallic;
layout(location = 1) out vec4 outNormalRoughnessAo;
layout(location = 2) out vec4 outEmissiveClearcoat;
layout(location = 3) out vec2 outMotion;   // see Passes/Motion.cs
layout(location = 4) out vec4 outBaked;    // baked diffuse light over pi, alpha how much the bake lights it; see writeBaked
layout(location = 5) out vec4 outShadowMask; // the baked shadow mask, 1 where nothing is masked

// The motion of a point that was behind last frame's camera: far off any picture, near the most a 16-bit float holds.
const float MOTION_OFF_PICTURE = -60000.0;

struct Surface {
    vec3 baseColor;
    float metallic;
    float roughness;
    vec3 normal; // World space, unit length.
    vec3 emissive;
    float clearcoat;
};

// Foliage writes its translucency, the material's transmission, as a negative number, and at least
// this much, so foliage that lets no light through is still marked as foliage.
const float FOLIAGE_MARK = 1.0 / 1024.0;

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
// 0.5 is flat, so a little rounding stays clean. With the baker's 25 mm reach, an edge wears
// fully for about a centimetre and fades out by about two.
const float WEAR_FROM = 0.55;
const float WEAR_FULL = 0.8;

// Dirt starts where the sky the mask saw drops below 1 minus the first of these and is whole
// below 1 minus the second, so a face beside a wall stays clean and only true hollows darken.
const float DIRT_FROM = 0.25;
const float DIRT_FULL = 0.75;

// Lays the material's wear and dirt over a colour and roughness, by the masks baked from its
// mesh and read through the second uv. Wear goes on convex edges, where the mask's breakup
// lets it, and dirt in hollows the sky can't reach and low down near the mesh's foot, but not
// over wear, which rubs it off. Each tints rather than paints: the colour is multiplied by
// twice the layer's, so the grain underneath still shows. Graphics.WearAndDirt turns it off.
void applyLayers(Material material, inout vec3 color, inout float roughness) {
    if (isOff(OFF_WEAR_AND_DIRT) || (material.wear.a <= 0.0 && material.dirt.a <= 0.0)) {
        return;
    }

    vec4 mask = texture(textures[material.maskTexture], inUv2, frame.mipBias);
    float wear = smoothstep(WEAR_FROM, WEAR_FULL, mask.r) * mask.a * material.wear.a;
    float hollow = smoothstep(DIRT_FROM, DIRT_FULL, 1.0 - mask.g);
    float dirt = max(hollow, clamp(1.0 - mask.b, 0.0, 1.0)) * material.dirt.a * (1.0 - wear);

    color = mix(color, clamp(color * material.wear.rgb * 2.0, 0.0, 1.0), wear);
    if (material.wearRoughness >= 0.0) {
        roughness = mix(roughness, material.wearRoughness, wear);
    }

    color = mix(color, clamp(color * material.dirt.rgb * 2.0, 0.0, 1.0), dirt);
    if (material.dirtRoughness >= 0.0) {
        roughness = mix(roughness, material.dirtRoughness, dirt);
    }
}

// How far below the level, in metres, a surface goes from dry of water to all water, so the
// shore is soft. Must match Puddles.Shore in Passes/Puddles.cs.
const float PUDDLE_SHORE = 0.003;

// Standing water: a near mirror. Shallow, it shows the wet ground under it. Deeper, the silt it
// holds hides the bottom, and by PUDDLE_DEEP metres it is the near-black brown of turbid water,
// which takes in nearly all the light that gets into it. So the sky and what stands round it
// read in it as a reflection on a dark sheet, as they do in a real puddle: the reflection is
// only a fifth of the light at a standing eye's angle, and a paler body would wash it out. The
// lighting lets the body show only through the share the reflection leaves (WATER_F0).
const float PUDDLE_ROUGHNESS = 0.03;
const float PUDDLE_DEEP = 0.04;
const vec3 PUDDLE_TURBID = vec3(0.01, 0.0085, 0.006);

// The ripples: two layers of drifting, changing waves, each RIPPLE_AMPLITUDE metres high. The
// first is broad and slow, the second fine and quicker. Each moves one wavelength a cycle.
const float RIPPLE_AMPLITUDE = 0.001;
const float RIPPLE_SLOW_HZ = 0.4;
const float RIPPLE_SLOW_WAVELENGTH = 0.3;
const float RIPPLE_QUICK_HZ = 0.9;
const float RIPPLE_QUICK_WAVELENGTH = 0.11;

// The slope, along x and z, of one layer of ripples at a point on the water.
vec2 rippleSlope(vec2 position, float wavelength, float hz, vec2 drift) {
    float cycles = frame.time * hz;
    vec3 at = vec3(position / wavelength + drift * cycles, cycles);
    const float delta = 0.05;
    float here = worldNoise(at);
    float alongX = worldNoise(at + vec3(delta, 0.0, 0.0));
    float alongZ = worldNoise(at + vec3(0.0, delta, 0.0));
    // The noise runs 0 to 1, so the wave is twice the amplitude from trough to crest.
    return vec2(alongX - here, alongZ - here) / delta * (2.0 * RIPPLE_AMPLITUDE / wavelength);
}

// Lays the frame's puddles over a surface whose material has MATERIAL_PUDDLES. The surface's height
// at this pixel is its world height less how far its height map sinks it, so the bottom of a groove
// sits below its face. Below frame.puddles.x it is water: rippled, smooth, not metal and darker with silt the
// deeper it gets. For frame.puddles.y metres above, it is damp: at the waterline its colour is squared
// and its roughness halved, fading in a straight line to dry. Passes/Puddles.cs does the same sums.
// A pixel that is at least half water sets puddleWater, and writeSurface marks it as water.
float puddleWater = 0.0;

void applyPuddles(inout Surface surface, vec3 worldPos, float height, float heightDepth) {
    float surfaceHeight = worldPos.y + (height - 1.0) * heightDepth;
    float level = frame.puddles.x;
    float band = frame.puddles.y;
    float below = level - surfaceHeight;
    float water = clamp(below / PUDDLE_SHORE, 0.0, 1.0);
    float damp = band > 0.0 ? clamp(1.0 + below / band, 0.0, 1.0) : (below > 0.0 ? 1.0 : 0.0);
    if (damp <= 0.0) {
        return;
    }

    surface.baseColor = mix(surface.baseColor, surface.baseColor * surface.baseColor, damp);
    surface.roughness = mix(surface.roughness, surface.roughness * 0.5, damp);
    if (water <= 0.0) {
        return;
    }

    vec2 slope = rippleSlope(worldPos.xz, RIPPLE_SLOW_WAVELENGTH, RIPPLE_SLOW_HZ, vec2(0.8, 0.6))
        + rippleSlope(worldPos.xz + 31.0, RIPPLE_QUICK_WAVELENGTH, RIPPLE_QUICK_HZ, vec2(-0.5, 0.87));
    vec3 rippled = normalize(vec3(-slope.x, 1.0, -slope.y));
    vec3 silty = mix(surface.baseColor, PUDDLE_TURBID, clamp(below / PUDDLE_DEEP, 0.0, 1.0));

    surface.baseColor = mix(surface.baseColor, silty, water);
    surface.roughness = mix(surface.roughness, PUDDLE_ROUGHNESS, water);
    surface.metallic = mix(surface.metallic, 0.0, water);
    surface.normal = normalize(mix(surface.normal, rippled, water));
    puddleWater = water;
}

// What to write in the normal target's alpha: WATER_SIDE for the front of puddle water, 1 for the rest.
float waterSide() {
    return puddleWater >= 0.5 ? WATER_SIDE : 1.0;
}

// Whether this pixel's material gets wet and the frame has puddles to wet it.
bool puddlesHere() {
    return (materials[draw.material].flags & MATERIAL_PUDDLES) != 0u && frame.puddles.z > 0.5 && !isOff(OFF_PUDDLES);
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

    // Without a level set, or with Graphics.Puddles off, frame.puddles.z is 0 and nothing is wet.
    if (puddlesHere()) {
        float height = material.heightDepth > 0.0 ? textureGrad(textures[material.heightTexture], uv, uvX, uvY).r : 1.0;
        applyPuddles(surface, inPosition, height, material.heightDepth);
    }

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

// What foliage writes in the clearcoat channel: its translucency as a negative number.
float foliageChannel(Material material) {
    return -max(material.transmission, FOLIAGE_MARK);
}

// Writes the baked light a copy with a lightmap gets, turned to the normal it is lit with, and its shadow mask. The
// irradiance page holds light with pi in, so dividing by pi gives what a white surface sends back, as the lighting
// wants. The direction page holds the way the light mostly came from, d, as d * 0.5 + 0.5, with |d| how much of it came
// that way. The baker gathered the light over the mesh's own normal, so the light is turned by the share the normal map
// faces d against the share that normal does: a surface that faces the light gets more, one that faces away less, and
// under an even sky, where d is near nothing, it is unchanged. Irradiance alpha is never read, since BC6H drops it.
// A copy with no lightmap, and a ghost, writes no light, which the lighting reads as lit live, and a mask of 1.
//
// The baker gathers over the geometric normal, so d stays in its half of the sphere and the vertex normal's share is at
// least 0.5 there. But a smooth-shaded mesh's vertex normal leans away from the geometric one, and a texel at a chart's
// edge is filled from its neighbours, so d can point behind the vertex normal and the share fall towards 0, which would
// turn the light up thousands of times into a bright speck, or infinity. The share is floored at 0.5, its least for a d
// the baker could have made, so light from behind counts as light from the side: the gain is never more than 2, the
// most a normal map can turn up light that came along the surface, and a d in front is turned exactly as before.
//
// A copy of a chunk (inLightmap.w at least 0) also gets the frame's sky and suns, through its bake's transfer, which is in
// the chunk's own space: the sky's and suns' L1 sets are turned into that space and dotted with it, so every copy is
// lit for the way it faces. Its pages' direction is in that space too, and is turned back to the world. A chunk with no
// plain light has no irradiance or direction page, and its d is the transfer's own lean towards the sky that lights it.
// See "Chunk transfer" in docs/design.md.
//
// Alpha is how much of the pixel the bake lights: exactly 1 for a space's lightmap, and chunkEdgeWeight for a chunk's,
// which falls to 0 at its sides. The light itself is never weighted; the lighting blends it with the live light.
const float LIGHTMAP_PI = 3.14159265359;
const float LIGHTMAP_LEAST_SHARE = 0.5;
const vec3 LIGHTMAP_LUMINANCE = vec3(0.2126, 0.7152, 0.0722);

// The irradiance, with pi in, that the frame's sky and suns give a chunk's texel, and in towards the way its sky light
// leans, in the chunk's own space, unnormalised.
vec3 chunkLight(int c, out vec3 towards) {
    ChunkData chunk = chunkData.chunks[c];
    vec4 skyR = textureLod(textures[nonuniformEXT(chunk.transfer.x)], inLightmapUv, 0.0);
    vec4 skyG = textureLod(textures[nonuniformEXT(chunk.transfer.y)], inLightmapUv, 0.0);
    vec4 skyB = textureLod(textures[nonuniformEXT(chunk.transfer.z)], inLightmapUv, 0.0);
    vec4 sun = textureLod(textures[nonuniformEXT(chunk.transfer.w)], inLightmapUv, 0.0);
    vec3 tint = textureLod(textures[nonuniformEXT(chunk.pages.x)], inLightmapUv, 0.0).rgb;

    // The sky's sets per colour, and its luminance's, each coefficient's colours in one vec4 of the buffer.
    mat4 sky = mat4(skySh.coefficients[0], skySh.coefficients[1], skySh.coefficients[2], skySh.coefficients[3]);
    vec4 lightR = intoChunkSh(c, vec4(sky[0].r, sky[1].r, sky[2].r, sky[3].r));
    vec4 lightG = intoChunkSh(c, vec4(sky[0].g, sky[1].g, sky[2].g, sky[3].g));
    vec4 lightB = intoChunkSh(c, vec4(sky[0].b, sky[1].b, sky[2].b, sky[3].b));
    vec4 lightL = intoChunkSh(c, vec4(sky[0].a, sky[1].a, sky[2].a, sky[3].a));
    vec3 light = vec3(dot(skyR, lightR), dot(skyG, lightG), dot(skyB, lightB)) * frame.backgroundRadiance.a;

    vec4 sunR = intoChunkSh(c, vec4(chunkData.sun[0].r, chunkData.sun[1].r, chunkData.sun[2].r, chunkData.sun[3].r));
    vec4 sunG = intoChunkSh(c, vec4(chunkData.sun[0].g, chunkData.sun[1].g, chunkData.sun[2].g, chunkData.sun[3].g));
    vec4 sunB = intoChunkSh(c, vec4(chunkData.sun[0].b, chunkData.sun[1].b, chunkData.sun[2].b, chunkData.sun[3].b));
    light += tint * vec3(dot(sun, sunR), dot(sun, sunG), dot(sun, sunB));

    // The luminance transfer's vector part, weighted by the turned sky's, both as (x, y, z).
    vec4 transfer = skyR * LIGHTMAP_LUMINANCE.r + skyG * LIGHTMAP_LUMINANCE.g + skyB * LIGHTMAP_LUMINANCE.b;
    towards = vec3(transfer.w * lightL.w, transfer.y * lightL.y, transfer.z * lightL.z);
    return max(light, vec3(0.0));
}

// The frame's four mask slots from one bake's mask page: slot s is the page's channel table[s], or 1, the bake saying
// nothing, where the table has -1. A light's shadow there is then its real-time one alone. With no page every slot is 1.
// See "Shadow mask channels" in docs/design.md.
vec4 frameMaskSlots(int page, ivec4 table) {
    vec4 mask = page < 0 ? vec4(1.0) : textureLod(textures[nonuniformEXT(page)], inLightmapUv, 0.0);
    vec4 slots;
    for (int s = 0; s < 4; s++) {
        slots[s] = table[s] < 0 ? 1.0 : mask[table[s]];
    }

    return slots;
}

// How much of a chunk's pixel the bake lights, which writeBaked puts in the baked alpha for the lighting to blend by: 1
// deeper than the chunk's edge blend (min.w) in from its four sides, its own ±x and ±z, falling to 0 at them by a
// smoothstep of the distance to the nearest. Not the top or bottom, where a chunk's floor and roof usually sit. Two
// chunks that touch both reach the live light at the face they share, so they meet without a step. An edge blend of 0
// is a hard edge. See "Edge bands" in docs/design.md.
float chunkEdgeWeight(int c) {
    ChunkData chunk = chunkData.chunks[c];
    float blend = chunk.min.w;
    if (blend <= 0.0) {
        return 1.0;
    }

    vec2 fromMin = inChunkPosition.xz - chunk.min.xz;
    vec2 fromMax = chunk.max.xz - inChunkPosition.xz;
    float nearest = max(min(min(fromMin.x, fromMin.y), min(fromMax.x, fromMax.y)), 0.0);
    return smoothstep(0.0, blend, nearest);
}

void writeBaked(vec3 normal) {
    int chunk = inLightmap.w;
    if (inLightmap.x < 0 && chunk < 0) {
        outBaked = vec4(0.0);
        outShadowMask = vec4(1.0);
        return;
    }

    // The pages have one level each, so they are read at it.
    vec3 irradiance = vec3(0.0);
    vec3 d = vec3(0.0);
    if (inLightmap.x >= 0) {
        irradiance = textureLod(textures[nonuniformEXT(inLightmap.x)], inLightmapUv, 0.0).rgb;
        d = textureLod(textures[nonuniformEXT(inLightmap.y)], inLightmapUv, 0.0).xyz * 2.0 - 1.0;
    }

    vec3 vertexNormal = surfaceFrame()[2];
    if (chunk >= 0) {
        vec3 towards;
        irradiance += chunkLight(chunk, towards);
        if (inLightmap.x < 0) {
            float length2 = dot(towards, towards);
            d = length2 > 0.0 ? towards * inversesqrt(length2) : vec3(0.0);
        }

        d = outOfChunkDirection(chunk, d);

        // Light only reaches a surface from in front, as a space bake's way always is, but the transfer's lean is the
        // way its light escaped, not the way it came in, and can point behind. There it would turn the light off, so
        // only the part along the surface is kept. See "Chunk transfer" in docs/design.md.
        if (inLightmap.x < 0) {
            d -= min(dot(d, vertexNormal), 0.0) * vertexNormal;
        }
    }

    float turned = (dot(normal, d) * 0.5 + 0.5) / max(LIGHTMAP_LEAST_SHARE, dot(vertexNormal, d) * 0.5 + 0.5);
    outBaked = vec4(irradiance / LIGHTMAP_PI * turned, chunk >= 0 ? chunkEdgeWeight(chunk) : 1.0);
    outShadowMask = chunk >= 0
        ? frameMaskSlots(chunkData.chunks[chunk].pages.y, chunkData.chunks[chunk].maskSlots)
        : frameMaskSlots(inLightmap.z, chunkData.spaceSlots);
}

// Applies the draw's tint, so every surface shader gets it. Foliage has no clearcoat, so
// its translucency travels in the same channel as a negative number, and a subsurface
// material puts its index there, plus SUBSURFACE_MARK, so the lighting and blur find it.
// A ghost keeps only the surface's shape: one matte colour that glows a little,
// whatever the material.
// Every surface also writes its motion: in scene pixels, from where it is now back to where it was, as Motion.Pack does.
void writeSurface(Surface surface) {
    // Behind last frame's camera there's nowhere to point, so it points far off the picture, which has no history.
    outMotion = inClipBefore.z > 0.0
        ? (inClipBefore.xy / inClipBefore.z - inClipNow.xy / inClipNow.z) * 0.5 * frame.screen.xy
        : vec2(MOTION_OFF_PICTURE);
    Material material = materials[draw.material];

    if (draw.ghost.a > 0.0) {
        vec3 color = clamp(draw.ghost.rgb, 0.0, 1.0);
        outBaseColorMetallic = vec4(color, 0.0);
        outNormalRoughnessAo = vec4(octahedralEncode(surface.normal), 0.7, 1.0);
        outEmissiveClearcoat = vec4(color * GHOST_GLOW, 0.0);
        outBaked = vec4(0.0);
        outShadowMask = vec4(1.0);
        return;
    }

    bool foliage = (material.flags & MATERIAL_FOLIAGE) != 0u;

    // With materials off, every surface is the same plain clay, shaped only by its mesh: no
    // colour, tint, normal map, shine, coat or subsurface. Cut-outs still cut and glows still glow.
    // Puddles are the frame's weather, not the material's, so the clay still gets wet, at the
    // height of its mesh, as there is no height map to sink it.
    if (isOff(OFF_MATERIALS)) {
        Surface plain;
        plain.baseColor = PLAIN_COLOR;
        plain.metallic = 0.0;
        plain.roughness = PLAIN_ROUGHNESS;
        plain.normal = surfaceFrame()[2];
        puddleWater = 0.0;
        if (puddlesHere()) {
            applyPuddles(plain, inPosition, 1.0, 0.0);
        }

        outBaseColorMetallic = vec4(plain.baseColor, plain.metallic);
        outNormalRoughnessAo = vec4(octahedralEncode(plain.normal), plain.roughness, waterSide());
        outEmissiveClearcoat = vec4(surface.emissive, foliage ? foliageChannel(material) : 0.0);
        writeBaked(plain.normal);
        return;
    }

    float channel = foliage ? foliageChannel(material) : surface.clearcoat;
    if (material.subsurface.w > 0.0) {
        channel = SUBSURFACE_MARK + float(draw.material);
    }
    outBaseColorMetallic = vec4(surface.baseColor * draw.tint.rgb, surface.metallic);
    outNormalRoughnessAo = vec4(octahedralEncode(surface.normal), surface.roughness, waterSide());
    outEmissiveClearcoat = vec4(surface.emissive, channel);
    writeBaked(surface.normal);
}
