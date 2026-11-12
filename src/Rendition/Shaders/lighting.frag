#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "brdf.glsl"
#include "sampling.glsl"
#include "probes.glsl"
#include "reflection_probes.glsl"
#include "water.glsl"
#include "material_data.glsl"
#include "light_data.glsl"
#include "local_shadows.glsl"
#include "sky.glsl"
#include "fog.glsl"

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D baseColorMetallicTexture;
layout(set = 1, binding = 1) uniform sampler2D normalRoughnessAoTexture;
layout(set = 1, binding = 2) uniform sampler2D depthTexture;
layout(set = 1, binding = 3) uniform sampler2DArray shadowDepthTexture;
layout(set = 1, binding = 4) uniform sampler2DArrayShadow shadowCompareTexture;
layout(set = 1, binding = 5) uniform samplerCube reflectionTexture;
layout(set = 1, binding = 6) uniform samplerCube irradianceTexture;
layout(set = 1, binding = 7) uniform sampler2D emissiveClearcoatTexture;
layout(set = 1, binding = 8) uniform sampler2D ambientOcclusionTexture;
layout(set = 1, binding = 9) uniform sampler2D historyTexture;
layout(set = 1, binding = 10) uniform samplerCubeArray probeTexture;
// The G-buffer's baked light and shadow mask; see writeBaked in surface.glsl. Alpha 1 in the baked light marks a pixel
// with a lightmap, whose soft light comes from it instead of the probes or the sky.
layout(set = 1, binding = 13) uniform sampler2D bakedTexture;
layout(set = 1, binding = 14) uniform sampler2D shadowMaskTexture;

// How much of the screen's occlusion a lightmapped pixel's soft light still takes. The bake already saw the creases it
// could resolve, so taking all of it would darken them twice; the screen still adds the contact it is too coarse for.
const float LIGHTMAP_OCCLUSION = 0.5;

// A Mixed light's visibility on a lightmapped pixel, which the bake's shadow mask also measured in one channel. With a
// real-time shadow too it takes the darker of the two, so a moving thing's shadow still falls and the mask covers what
// the shadow map can't, such as past the sun's last cascade, where shadowVisibility gives 1. A light without a shadow
// map takes the mask alone. A light without a channel (-1), or a pixel without a lightmap, keeps what it had: the
// mask's unused channels hold 0, so they must never be read.
float maskedVisibility(bool lightmapped, vec4 shadowMask, float channel, bool shadowed, float visibility) {
    if (!lightmapped || channel < 0.0) {
        return visibility;
    }

    float mask = shadowMask[int(channel + 0.5)];
    return shadowed ? min(mask, visibility) : mask;
}

layout(std430, set = 2, binding = 1) readonly buffer Materials {
    Material materials[];
};

layout(location = 0) out vec4 outColor;

// The diffuse light of subsurface materials, which sss_blur.frag spreads and adds to
// outColor. Alpha holds the material's index plus one, or 0 for every other pixel.
layout(location = 1) out vec4 outScatter;

const int BLOCKER_SAMPLES = 16;
const int SHADOW_SAMPLES = 24;

// Contact shadows walk this many steps towards the sun, and count a surface as in the way
// when the ray passes more than this many metres behind it.
const int CONTACT_STEPS = 12;
const float CONTACT_THICKNESS = 0.02;
// A surface more than this far in front of the ray is a nearer object passing in front, not
// something touching, and the shadow maps already cover it.
const float CONTACT_DEPTH_LIMIT = 0.15;

// The clear lacquer over car paint is very smooth and reflects like glass.
const float CLEARCOAT_ROUGHNESS = 0.06;
const vec3 CLEARCOAT_F0 = vec3(0.04);

// How reflected rays walk through the depth buffer. Steps grow with distance, because
// far away precision matters less. Metres, except for the growth.
const int TRACE_STEPS = 56;
const int TRACE_REFINE_STEPS = 5;
const float TRACE_FIRST_STEP = 0.02;
const float TRACE_GROWTH = 1.07;
const float TRACE_DISTANCE = 12.0;
const float TRACE_THICKNESS = 0.25;
const float TRACE_MAX_ROUGHNESS = 0.45;
const float TRACE_MAX_RADIANCE = 16.0;

// Light that bounces between the walls of a crease before it escapes, which plain
// occlusion leaves out. Pale surfaces pass most of it on, so their creases darken less
// and take on their own colour; dark surfaces absorb it and keep the plain result.
// Jimenez et al., "Practical Real-Time Strategies for Accurate Indirect Occlusion"
// (2016), fitted the curve to ray-traced scenes.
vec3 multiBounceOcclusion(float visibility, vec3 albedo) {
    vec3 a = 2.0404 * albedo - 0.3324;
    vec3 b = -4.7951 * albedo + 0.6417;
    vec3 c = 2.7552 * albedo + 0.6903;
    return max(vec3(visibility), ((visibility * a + b) * visibility + c) * visibility);
}

// How much of the environment's reflection gets out of a crease. A reflection leaves in a
// narrow cone, which a mirror-like surface can aim through the opening, while a rough one
// spreads into the walls; at grazing angles it skims along them. Lagarde and de
// Rousiers, "Moving Frostbite to Physically Based Rendering" (2014).
float specularOcclusion(float nDotV, float ao, float roughness) {
    return clamp(pow(nDotV + ao, exp2(-16.0 * roughness - 1.0)) - 1.0 + ao, 0.0, 1.0);
}

// The soft light reaching a point from around a direction, in the lit image's units: from the
// light probes where the frame uses them and they see the point, otherwise from the sky.
vec3 softLight(vec3 position, vec3 direction, vec3 v) {
    if (frame.lightProbeCounts.w > 0.5 || frame.probeCascadeCounts.w > 0.5) {
        vec3 fromProbes = probeIrradiance(position, direction, v);
        if (fromProbes.x >= 0.0) {
            return fromProbes;
        }
    }

    return texture(irradianceTexture, direction).rgb * frame.backgroundRadiance.a;
}

// What a surface is made of and how it's seen, for lighting it one light at a time.
struct Surface {
    vec3 n;
    vec3 v;
    float nDotV;
    float alpha;
    vec3 f0;
    vec3 multiscatter; // What specular is multiplied by for the light that bounces between facets.
    vec3 baseColor;
    float metallic;
    float clearcoat;
    float translucency;
    vec3 transmissionTint;
};

// The light a surface sends to the eye from a light arriving along l, for each unit of
// the light's brightness, kept as diffuse and specular so subsurface materials can blur
// the diffuse: a glossy highlight, the diffuse colour beneath, the coat over both, and
// what shines through thin parts from behind. False when none of it arrives.
bool shade(Surface s, vec3 l, out vec3 diffuseLit, out vec3 specularLit) {
    diffuseLit = vec3(0.0);
    specularLit = vec3(0.0);
    float facing = dot(s.n, l);
    float nDotL = max(facing, 0.0);

    // Leaves lit from behind glow with their own colour.
    float transmitted = s.translucency * max(-facing, 0.0);
    if (nDotL == 0.0 && transmitted == 0.0) {
        return false;
    }

    vec3 h = normalize(s.v + l);
    vec3 f = fresnelSchlick(max(dot(s.v, h), 0.0), s.f0);
    vec3 specular = distributionGgx(max(dot(s.n, h), 0.0), s.alpha) * visibilitySmithGgx(s.nDotV, nDotL, s.alpha) * f * s.multiscatter;
    vec3 diffuse = (1.0 - f) * (1.0 - s.metallic) * s.baseColor / PI;

    diffuseLit = diffuse * nDotL;
    specularLit = specular * nDotL;
    if (s.clearcoat > 0.0) {
        float coatAlpha = CLEARCOAT_ROUGHNESS * CLEARCOAT_ROUGHNESS;
        vec3 coatF = fresnelSchlick(max(dot(s.v, h), 0.0), CLEARCOAT_F0) * s.clearcoat;
        float coatHighlight = distributionGgx(max(dot(s.n, h), 0.0), coatAlpha) * visibilitySmithGgx(s.nDotV, nDotL, coatAlpha);
        diffuseLit *= 1.0 - coatF;
        specularLit = specularLit * (1.0 - coatF) + coatHighlight * coatF * nDotL;
    }

    diffuseLit += (1.0 - s.metallic) * s.baseColor / PI * transmitted * s.transmissionTint;
    return true;
}

// How far inside a cascade's map the point is, as a share of the map from its nearest
// edge, leaving room for the filter: below 0 is outside.
float cascadeMargin(int c, vec3 position, float inset) {
    vec4 projected = frame.shadowMatrices[c] * vec4(position, 1.0);
    vec2 uv = projected.xy * 0.5 + 0.5;
    vec2 margins = min(uv - inset, 1.0 - inset - uv);
    return projected.z > 0.0 && projected.z < 1.0 ? min(margins.x, margins.y) : -1.0;
}

// The first cascade whose map holds the point with room for the filter around it, or -1
// when none does. Near the edge of its map a point hands over to the next cascade, more
// often the nearer it is, so the line where the sharpness changes melts into noise that
// anti-aliasing smooths. In the last map, edge says how far into that band it is.
int shadowCascade(vec3 position, float inset, out float edge) {
    edge = 0.0;
    float band = frame.shadowFade.z;
    for (int c = 0; c < frame.shadowCascades; c++) {
        float margin = cascadeMargin(c, position, inset);
        if (margin <= 0.0) {
            continue;
        }

        float handOver = 1.0 - clamp(margin / band, 0.0, 1.0);
        if (c + 1 == frame.shadowCascades) {
            edge = handOver;
            return c;
        }

        bool next = handOver > 0.0 && frameNoise(gl_FragCoord.xy + 37.0, frame.frameNumber) < handOver
            && cascadeMargin(c + 1, position, inset) > 0.0;
        return next ? c + 1 : c;
    }
    return -1;
}

// How far shadows have faded out at a point, from 0 to 1: with distance from the camera,
// so they end smoothly rather than at a line, and at the edge of the last map.
float shadowFadeOut(vec3 position, float edge) {
    float distance = length(position - frame.cameraPosition.xyz);
    float far = frame.shadowFade.x < frame.shadowFade.y
        ? smoothstep(frame.shadowFade.x, frame.shadowFade.y, distance) : 0.0;
    return max(far, smoothstep(0.0, 1.0, edge));
}

// Soft shadow that is sharp where an object touches the surface and blurs with
// distance, like a real studio light. Returns 1 for fully lit.
float shadowVisibility(vec3 position, vec3 n, vec3 l) {
    float lightAngle = frame.parameters.z;
    float nDotL = clamp(dot(n, l), 0.05, 1.0);
    float texel = 1.0 / float(textureSize(shadowDepthTexture, 0).x);

    float edge;
    int cascade = shadowCascade(position, 2.0 * texel, edge);
    if (cascade < 0) {
        return 1.0;
    }

    float fadeOut = shadowFadeOut(position, edge);
    if (fadeOut >= 1.0) {
        return 1.0;
    }

    // Moving the point two texels off its surface stops it shadowing itself.
    float normalOffset = 4.0 * frame.shadowRadii[cascade] * texel;
    vec4 projected = frame.shadowMatrices[cascade] * vec4(position + n * normalOffset * (1.0 - nDotL), 1.0);
    vec2 uv = projected.xy * 0.5 + 0.5;
    float depth = projected.z;
    if (depth <= 0.0 || depth >= 1.0) {
        return 1.0;
    }

    // One unit across the map covers as far as depthScale units of depth do, so a slope's
    // bias is the sample offset times the slope, times depthScale.
    float depthScale = frame.shadowDepthScales[cascade];
    float slope = min(sqrt(1.0 - nDotL * nDotL) / nDotL, 2.0);
    float layer = float(cascade);
    float rotation = frameNoise(gl_FragCoord.xy, frame.frameNumber) * 2.0 * PI;
    float searchRadius = max(lightAngle * 0.2, 2.0 * texel);

    // Reversed-Z: blockers sit nearer the light, which means a larger depth.
    float blockerSum = 0.0;
    int blockers = 0;
    for (int i = 0; i < BLOCKER_SAMPLES; i++) {
        vec2 offset = diskSample(i, BLOCKER_SAMPLES, rotation) * searchRadius;
        float stored = texture(shadowDepthTexture, vec3(uv + offset, layer)).r;
        if (stored > depth + (2.0 * texel + searchRadius * slope) * depthScale) {
            blockerSum += stored;
            blockers++;
        }
    }
    if (blockers == 0) {
        return 1.0;
    }

    float penumbra = clamp((blockerSum / float(blockers) - depth) / depthScale * lightAngle, 1.5 * texel, searchRadius);
    float reference = depth + (2.0 * texel + penumbra * slope) * depthScale;
    float visibility = 0.0;
    for (int i = 0; i < SHADOW_SAMPLES; i++) {
        vec2 offset = diskSample(i, SHADOW_SAMPLES, rotation) * penumbra;
        visibility += texture(shadowCompareTexture, vec4(uv + offset, layer, reference));
    }
    return mix(visibility / float(SHADOW_SAMPLES), 1.0, fadeOut);
}

// What the reflection probes, or the studio where there are none, show in a direction.
vec3 sampleReflection(vec3 position, vec3 direction, float lod) {
    return probeReflection(probeTexture, reflectionTexture, position, direction, lod);
}

// Tilts a mirror reflection the way a slightly rough surface scatters it. One random
// tilt per pixel per frame turns into a smooth blur once frames are averaged.
vec3 scatterReflection(vec3 v, vec3 n, float alpha, vec2 random) {
    float cosTheta = sqrt((1.0 - random.y) / (1.0 + (alpha * alpha - 1.0) * random.y));
    float sinTheta = sqrt(1.0 - cosTheta * cosTheta);
    float phi = 2.0 * PI * random.x;
    vec3 tangent = normalize(cross(abs(n.y) < 0.99 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0), n));
    vec3 bitangent = cross(n, tangent);
    vec3 h = tangent * sinTheta * cos(phi) + bitangent * sinTheta * sin(phi) + n * cosTheta;
    vec3 direction = reflect(-v, h);
    return dot(direction, n) > 0.0 ? direction : reflect(-v, n);
}

// How far a point is behind what the camera sees there. Under perspective, clip z is the
// same everywhere and reversed-Z stores clip z / w, so dividing recovers the distance of
// the stored surface. Under orthographic, w is always 1 and depth is even, so the
// difference only needs scaling by how much depth one world unit is worth.
float depthGap(vec4 clip, out vec2 uv) {
    uv = clip.xy / clip.w * 0.5 + 0.5;
    float stored = texture(depthTexture, uv).r;
    if (stored <= 0.0) {
        return -1.0;
    }

    mat4 m = frame.viewProjection;
    bool parallel = m[0][3] == 0.0 && m[1][3] == 0.0 && m[2][3] == 0.0;
    if (parallel) {
        return (stored - clip.z) / length(vec3(m[0][2], m[1][2], m[2][2]));
    }

    return clip.w - clip.z / stored;
}

// Light blocked by something touching the surface, nearer than the shadow maps' texels can
// tell: walks frame.shadowFade.w metres towards the light, seeing each step where the camera
// sees it, and darkens the light where the step is behind what the camera sees there. A
// surface far more in front of the ray than the ray is long is a nearer object passing
// across it, not something touching, so it doesn't count. Hits near the ray's end fade, as
// does the whole effect towards the edge of the first cascade, past which it isn't drawn.
// Returns 1 for fully lit.
float contactShadow(vec3 position, vec3 n, vec3 l) {
    float reach = frame.shadowFade.w;
    if (reach <= 0.0) {
        return 1.0;
    }

    // The first cascade's map spans its radius each way from its centre.
    vec4 inFirst = frame.shadowMatrices[0] * vec4(position, 1.0);
    float strength = 1.0 - smoothstep(0.9, 1.0, length(inFirst.xy));
    if (strength <= 0.0) {
        return 1.0;
    }

    float stepLength = reach / float(CONTACT_STEPS);
    float noise = frameNoise(gl_FragCoord.xy + 71.0, frame.frameNumber);
    vec3 start = position + n * 0.005;
    for (int i = 0; i < CONTACT_STEPS; i++) {
        float travelled = (float(i) + noise) * stepLength;
        vec4 clip = frame.viewProjection * vec4(start + l * travelled, 1.0);
        if (clip.w <= 0.0) {
            break;
        }

        vec2 uv;
        float gap = depthGap(clip, uv);
        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
            break;
        }

        if (gap > CONTACT_THICKNESS && gap < CONTACT_DEPTH_LIMIT) {
            float blocked = 1.0 - smoothstep(0.75, 1.0, travelled / reach);
            return 1.0 - blocked * strength;
        }
    }
    return 1.0;
}

// Screen-space reflection: walks along the ray until it passes behind something the
// camera sees, then reads that spot from the previous finished frame. rgb is the colour
// and a is how far to trust it, since rays leaving the screen know nothing.
vec4 traceReflection(vec3 position, vec3 n, vec3 direction, float noise) {
    if (frame.historyValid == 0) {
        return vec4(0.0);
    }

    vec3 start = position + n * 0.01;
    float stepLength = TRACE_FIRST_STEP;
    float last = 0.0;
    float travelled = stepLength * (0.5 + noise);
    for (int i = 0; i < TRACE_STEPS && travelled < TRACE_DISTANCE; i++) {
        vec4 clip = frame.viewProjection * vec4(start + direction * travelled, 1.0);
        if (clip.w <= 0.0) {
            break;
        }

        vec2 uv;
        float gap = depthGap(clip, uv);
        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
            break;
        }

        if (gap > 0.0 && gap < max(TRACE_THICKNESS, stepLength * 2.0)) {
            float nearer = last;
            float farther = travelled;
            for (int j = 0; j < TRACE_REFINE_STEPS; j++) {
                float middle = 0.5 * (nearer + farther);
                if (depthGap(frame.viewProjection * vec4(start + direction * middle, 1.0), uv) > 0.0) {
                    farther = middle;
                } else {
                    nearer = middle;
                }
            }

            // Hitting the back of a surface means the ray slipped through the depth buffer.
            depthGap(frame.viewProjection * vec4(start + direction * farther, 1.0), uv);
            vec3 hitNormal = octahedralDecode(texture(normalRoughnessAoTexture, uv).xy);
            if (dot(hitNormal, direction) < 0.0) {
                vec4 previous = frame.previousViewProjection * vec4(start + direction * farther, 1.0);
                vec2 historyUv = previous.xy / previous.w * 0.5 + 0.5;
                vec2 edges = smoothstep(vec2(0.0), vec2(0.06), min(historyUv, 1.0 - historyUv));
                float trust = edges.x * edges.y * (1.0 - smoothstep(0.7, 1.0, farther / TRACE_DISTANCE));
                return vec4(min(texture(historyTexture, historyUv).rgb, vec3(TRACE_MAX_RADIANCE)), trust);
            }
        }

        last = travelled;
        stepLength *= TRACE_GROWTH;
        travelled += stepLength;
    }

    return vec4(0.0);
}

// Where the camera's ray through this pixel crosses the puddles' water level. The depth
// buffer holds the ground under the water, and a reflection traced from there would run
// straight into the sides of its own hollow. Two points along the pixel's ray, at its depth
// and nearer, give the ray under any projection.
vec3 onWaterSurface(vec4 clip, vec3 ground) {
    vec4 nearer = frame.inverseViewProjection * vec4(clip.xy, min(clip.z * 2.0 + 1e-4, 1.0), 1.0);
    vec3 towardsCamera = nearer.xyz / nearer.w - ground;
    float rise = frame.puddles.x - ground.y;
    if (rise <= 0.0 || towardsCamera.y <= 1e-6) {
        return ground;
    }

    return ground + towardsCamera * (rise / towardsCamera.y);
}

void main() {
    float depth = texture(depthTexture, inUv).r;

    outScatter = vec4(0.0);

    // Reversed-Z: a depth of 0 is the far plane, so nothing was drawn here.
    if (depth == 0.0) {
        if (isUnderwater()) {
            vec4 ahead = frame.inverseViewProjection * vec4(inUv * 2.0 - 1.0, 1.0, 1.0);
            vec3 direction = normalize(ahead.xyz / ahead.w - frame.cameraPosition.xyz);
            outColor = vec4(throughWater(vec3(0.0), frame.cameraPosition.xyz + direction * NOTHING_DRAWN_DISTANCE), 1.0);
            return;
        }

        if (frame.showEnvironment == 1) {
            vec4 ahead = frame.inverseViewProjection * vec4(inUv * 2.0 - 1.0, 1.0, 1.0);
            vec3 direction = normalize(ahead.xyz / ahead.w - frame.cameraPosition.xyz);

            // A clear sky is worked out for each pixel, so the sun's disc is as sharp as the
            // screen. Below the horizon the baked floor is the same either way.
            vec3 background;
            if (frame.skySun.w > 0.0 && direction.y >= 0.0) {
                background = preethamRadiance(direction, frame.skySun.xyz, frame.skySun.w)
                    + sunDiscRadiance(direction, frame.skySun.xyz, frame.sunDisc);
            } else {
                background = textureLod(reflectionTexture, direction, 0.0).rgb;
            }

            outColor = vec4(fogOverSky(background * frame.backgroundRadiance.a, inUv), 1.0);
            return;
        }

        outColor = vec4(fogOverSky(frame.backgroundRadiance.rgb, inUv), 1.0);
        return;
    }

    vec4 baseColorMetallic = texture(baseColorMetallicTexture, inUv);
    vec4 normalRoughnessAo = texture(normalRoughnessAoTexture, inUv);

    vec3 baseColor = baseColorMetallic.rgb;
    float metallic = baseColorMetallic.a;
    vec3 n = octahedralDecode(normalRoughnessAo.xy);
    float roughness = clamp(normalRoughnessAo.z, 0.045, 1.0);
    // Left out, the occlusion pass never ran, so its target holds nothing to read.
    // Its xy hold the bent normal, in view space; z how much of the sky the point sees.
    bool occlusionOff = isOff(OFF_AMBIENT_OCCLUSION);
    vec4 screenOcclusion = occlusionOff ? vec4(0.5, 0.5, 1.0, 1.0) : texture(ambientOcclusionTexture, inUv);
    float screenAo = screenOcclusion.z;
    // Puddle water lies open under the sky. The occlusion pass only saw the hollow beneath it.
    bool water = normalRoughnessAo.w > 0.5 * (1.0 + WATER_SIDE);
    if (water) {
        screenAo = 1.0;
    }
    float ao = min(normalRoughnessAo.w, 1.0) * screenAo;

    // A point in a crease sees the sky through the opening, so its soft light comes from
    // along the bent normal. The bent normal is blurred, so the more open the point, the more
    // its own normal and the detail in it take over.
    vec3 bentNormal = n;
    if (!occlusionOff && !water) {
        vec3 bent = transpose(mat3(frame.view)) * octahedralDecode(screenOcclusion.xy * 2.0 - 1.0);
        bentNormal = normalize(mix(n, bent, 1.0 - screenAo) + n * 1e-3);
    }
    // Alpha holds the clearcoat, for foliage how much light shines through as a negative
    // number, or for a subsurface material its index plus SUBSURFACE_MARK.
    // writeSurface() in surface.glsl packs it.
    vec4 emissiveClearcoat = texture(emissiveClearcoatTexture, inUv);
    bool subsurface = emissiveClearcoat.a >= SUBSURFACE_MARK - 0.5;
    int materialIndex = subsurface ? int(emissiveClearcoat.a - SUBSURFACE_MARK + 0.5) : 0;
    float clearcoat = subsurface ? 0.0 : max(emissiveClearcoat.a, 0.0);
    float translucency = max(-emissiveClearcoat.a, 0.0);
    // Foliage is marked by a negative channel. Its cut-out leaves are too thin for the depth
    // buffer, which holds only the front leaf, so it gets no contact shadows.
    bool foliage = emissiveClearcoat.a < 0.0;

    // Light through a thin part of a subsurface material comes out in the colours that
    // travel furthest, which for skin is red.
    vec3 transmissionTint = vec3(1.0);
    if (subsurface) {
        vec4 spread = materials[materialIndex].subsurface;
        translucency = materials[materialIndex].transmission;
        transmissionTint = spread.rgb / max(spread.w, 1e-6);
    }

    vec4 clip = vec4(inUv * 2.0 - 1.0, depth, 1.0);
    vec4 world = frame.inverseViewProjection * clip;
    vec3 position = world.xyz / world.w;
    if (water) {
        position = onWaterSurface(clip, position);
    }

    vec3 v = normalize(frame.cameraPosition.xyz - position);
    float nDotV = max(dot(n, v), 1e-4);
    float alpha = roughness * roughness;
    vec3 f0 = mix(vec3(0.04), baseColor, metallic);
    // Puddle water reflects as water does, WATER_F0 straight on and nearly all at grazing, and
    // what is in it, the silt and the ground under it, shows only by the light the surface lets
    // back out: the share the reflection leaves. Puddles.WaterBody does the same sum.
    if (water) {
        f0 = vec3(WATER_F0);
        baseColor *= 1.0 - fresnelSchlick(nDotV, f0);
    }

    vec2 brdf = environmentBrdf(roughness, nDotV);
    vec3 multiscatter = isOff(OFF_MULTISCATTER_SPECULAR) ? vec3(1.0) : multiscatterCompensation(f0, nDotV, roughness);
    vec3 reflectedLight = sampleReflection(position, reflect(-v, n), roughness * REFLECTION_LODS);

    // Coated surfaces are traced for their mirror-like coat, which then serves both layers.
    float traceStrength = isOff(OFF_REFLECTIONS) ? 0.0
        : clearcoat > 0.0 ? 1.0 : 1.0 - smoothstep(TRACE_MAX_ROUGHNESS * 0.5, TRACE_MAX_ROUGHNESS, roughness);
    vec4 traced = vec4(0.0);
    if (traceStrength > 0.0) {
        // Two independent numbers per pixel, each visiting new values every frame.
        vec2 random = fract(vec2(
            interleavedGradientNoise(gl_FragCoord.xy) + float(frame.frameNumber % 64) * 0.7548777,
            interleavedGradientNoise(gl_FragCoord.yx + 47.0) + float(frame.frameNumber % 64) * 0.5698403));
        float scatter = clearcoat > 0.0 ? CLEARCOAT_ROUGHNESS * CLEARCOAT_ROUGHNESS : alpha;
        traced = traceReflection(position, n, scatterReflection(v, n, scatter, random), random.x);
        traced.rgb /= max(frame.backgroundRadiance.a, 1e-3);
        traced.a *= traceStrength;
    }

    // Probes and the environment can't see what hides them in a crease, so their
    // reflection is occluded. A traced reflection found what's in the way itself.
    reflectedLight *= specularOcclusion(nDotV, ao, roughness);
    reflectedLight = mix(reflectedLight, traced.rgb, traced.a);
    vec3 environmentSpecular = reflectedLight * (f0 * brdf.x + brdf.y) * multiscatter;
    // Soft light already carries the environment's intensity; the reflections don't yet.
    // A lightmapped pixel's soft light is baked, with the environment's intensity in it, and the screen's occlusion
    // weighs on it only by LIGHTMAP_OCCLUSION. Its reflections are occluded as any other pixel's.
    vec4 baked = texture(bakedTexture, inUv);
    bool lightmapped = baked.a > 0.5;
    vec4 shadowMask = lightmapped ? textureLod(shadowMaskTexture, inUv, 0.0) : vec4(1.0);
    float diffuseAo = lightmapped ? min(normalRoughnessAo.w, 1.0) * mix(1.0, screenAo, LIGHTMAP_OCCLUSION) : ao;
    vec3 diffuseColor = baseColor * (1.0 - metallic) * multiBounceOcclusion(diffuseAo, baseColor);
    vec3 environmentDiffuse = (lightmapped ? baked.rgb : softLight(position, bentNormal, v)) * diffuseColor;
    if (translucency > 0.0) {
        environmentDiffuse += softLight(position, -n, v) * diffuseColor * translucency * transmissionTint;
    }

    // Subsurface materials keep their diffuse light apart, for the blur to spread.
    float environmentIntensity = frame.backgroundRadiance.a;
    vec3 scattered = vec3(0.0);
    if (subsurface) {
        scattered = environmentDiffuse;
        environmentDiffuse = vec3(0.0);
    }

    vec3 environmentLight = environmentSpecular;

    // The coat reflects first and what it lets through lights the layers below.
    vec3 coatFresnel = fresnelSchlick(nDotV, CLEARCOAT_F0) * clearcoat;
    if (clearcoat > 0.0) {
        vec3 coatReflection = sampleReflection(position, reflect(-v, n), CLEARCOAT_ROUGHNESS * REFLECTION_LODS);
        coatReflection *= specularOcclusion(nDotV, ao, CLEARCOAT_ROUGHNESS);
        coatReflection = mix(coatReflection, traced.rgb, traced.a);
        environmentLight = environmentLight * (1.0 - coatFresnel) + coatReflection * coatFresnel;
        environmentDiffuse *= 1.0 - coatFresnel;
    }

    vec3 color = environmentLight * environmentIntensity + environmentDiffuse + emissiveClearcoat.rgb;

    // Subsurface materials keep the diffuse light of every light apart, for the blur.
    Surface surface = Surface(n, v, nDotV, alpha, f0, multiscatter, baseColor, metallic, clearcoat, translucency, transmissionTint);
    for (int i = 0; i < frame.lightCount; i++) {
        vec3 l = frame.lights[i].direction.xyz;
        vec3 diffuseLit;
        vec3 specularLit;
        if (!shade(surface, l, diffuseLit, specularLit)) {
            continue;
        }

        // A light without a shadow map would shine through everything, so nearby
        // surfaces are all that can block it. Light from behind is looked up from the
        // side it arrives on.
        vec3 lightSide = dot(n, l) >= 0.0 ? n : -n;
        float visibility = frame.lights[i].direction.w > 0.5
            ? shadowVisibility(position, lightSide, l) * (foliage ? 1.0 : contactShadow(position, lightSide, l))
            : screenAo;
        visibility = maskedVisibility(lightmapped, shadowMask, frame.lights[i].radiance.w, frame.lights[i].direction.w > 0.5, visibility);
        vec3 reaching = sunlightReaching(position, l, i) * visibility;
        if (subsurface) {
            scattered += diffuseLit * reaching;
            color += specularLit * reaching;
        } else {
            color += (diffuseLit + specularLit) * reaching;
        }
    }

    // Point and spot lights, only those listed for this pixel's cluster. Those without a
    // shadow are blocked by nearby surfaces only, as suns without one are.
    uint cluster = clusterAt(gl_FragCoord.xy, position);
    uint localCount = clusterLightCount(cluster);
    for (uint i = 0; i < localCount; i++) {
        LocalLight light = localLights[clusterLight(cluster, i)];
        vec3 l;
        float reaching = localLightReaching(light, position, l);
        if (reaching <= 0.0) {
            continue;
        }

        vec3 diffuseLit;
        vec3 specularLit;
        if (!shade(surface, l, diffuseLit, specularLit)) {
            continue;
        }

        vec3 lightSide = dot(n, l) >= 0.0 ? n : -n;
        bool shadowed = light.radianceShadow.w >= 0.0;
        float visibility = shadowed ? localShadow(light, position, lightSide, l) : screenAo;
        visibility = maskedVisibility(lightmapped, shadowMask, localMaskChannel(light), shadowed, visibility);
        vec3 arriving = light.radianceShadow.rgb * reaching * visibility;
        if (subsurface) {
            scattered += diffuseLit * arriving;
            color += specularLit * arriving;
        } else {
            color += (diffuseLit + specularLit) * arriving;
        }
    }

    // Water tints everything on its way to the camera, which the blur can't redo, so
    // under water the light isn't spread.
    if (isUnderwater()) {
        outColor = vec4(throughWater(color + scattered, position), 1.0);
        return;
    }

    // The fog between the surface and the camera dims it and adds its own light. The light kept
    // apart for the subsurface blur is dimmed alike, and the fog's light goes with the rest.
    vec4 fog = fogInFront(position, reflectionTexture);
    if (fog.a < 1.0) {
        color = color * fog.a + fog.rgb;
        scattered *= fog.a;
    }

    // Distant surfaces melt into the background, which hides the horizon of a studio floor.
    // When the environment is the background, each one melts into the sky behind it.
    float fadeStart = frame.parameters.x;
    float fadeEnd = frame.parameters.y;
    if (fadeEnd > fadeStart) {
        vec3 away = position - frame.cameraPosition.xyz;
        vec3 behind = frame.backgroundRadiance.rgb;
        if (frame.hazeColor.a > 0.5) {
            behind = frame.hazeColor.rgb;
        } else if (frame.showEnvironment == 1) {
            behind = skyBehind(reflectionTexture, normalize(away), frame.skySun) * frame.backgroundRadiance.a;
        }

        float fade = smoothstep(fadeStart, fadeEnd, hazeDistance(position));
        color = mix(color, behind, fade);
        scattered *= 1.0 - fade;
    }

    outColor = vec4(color, 1.0);
    if (subsurface) {
        outScatter = vec4(scattered, float(materialIndex) + 1.0);
    }
}
