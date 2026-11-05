#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "brdf.glsl"
#include "sampling.glsl"
#include "water.glsl"
#include "light_data.glsl"
#include "local_shadows.glsl"

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

layout(location = 0) out vec4 outColor;

const int BLOCKER_SAMPLES = 16;
const int SHADOW_SAMPLES = 24;

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

// What a surface is made of and how it's seen, for lighting it one light at a time.
struct Surface {
    vec3 n;
    vec3 v;
    float nDotV;
    float alpha;
    vec3 f0;
    vec3 baseColor;
    float metallic;
    float clearcoat;
    float translucency;
};

// The light a surface sends to the eye from a light arriving along l, for each unit of
// the light's brightness: a glossy highlight, the diffuse colour beneath, the coat over
// both, and what shines through leaves from behind.
vec3 shade(Surface s, vec3 l) {
    float facing = dot(s.n, l);
    float nDotL = max(facing, 0.0);
    float transmitted = s.translucency * max(-facing, 0.0);
    if (nDotL == 0.0 && transmitted == 0.0) {
        return vec3(0.0);
    }

    vec3 h = normalize(s.v + l);
    vec3 f = fresnelSchlick(max(dot(s.v, h), 0.0), s.f0);
    vec3 specular = distributionGgx(max(dot(s.n, h), 0.0), s.alpha) * visibilitySmithGgx(s.nDotV, nDotL, s.alpha) * f;
    vec3 diffuse = (1.0 - f) * (1.0 - s.metallic) * s.baseColor / PI;

    vec3 lit = (diffuse + specular) * nDotL;
    if (s.clearcoat > 0.0) {
        float coatAlpha = CLEARCOAT_ROUGHNESS * CLEARCOAT_ROUGHNESS;
        vec3 coatF = fresnelSchlick(max(dot(s.v, h), 0.0), CLEARCOAT_F0) * s.clearcoat;
        float coatHighlight = distributionGgx(max(dot(s.n, h), 0.0), coatAlpha) * visibilitySmithGgx(s.nDotV, nDotL, coatAlpha);
        lit = lit * (1.0 - coatF) + coatHighlight * coatF * nDotL;
    }

    return lit + (1.0 - s.metallic) * s.baseColor / PI * transmitted;
}

// The first cascade whose map holds the point with room for the filter around it,
// or -1 when none does.
int shadowCascade(vec3 position, float inset) {
    for (int c = 0; c < frame.shadowCascades; c++) {
        vec4 projected = frame.shadowMatrices[c] * vec4(position, 1.0);
        vec2 uv = projected.xy * 0.5 + 0.5;
        if (all(greaterThan(uv, vec2(inset))) && all(lessThan(uv, vec2(1.0 - inset)))
            && projected.z > 0.0 && projected.z < 1.0) {
            return c;
        }
    }
    return -1;
}

// Soft shadow that is sharp where an object touches the surface and blurs with
// distance, like a real studio light. Returns 1 for fully lit.
float shadowVisibility(vec3 position, vec3 n, vec3 l) {
    float lightAngle = frame.parameters.z;
    float nDotL = clamp(dot(n, l), 0.05, 1.0);
    float texel = 1.0 / float(textureSize(shadowDepthTexture, 0).x);

    int cascade = shadowCascade(position, 2.0 * texel);
    if (cascade < 0) {
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

    // In this projection one unit of depth covers the same distance as one unit of uv,
    // so a slope's bias is simply the sample offset times the slope.
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
        if (stored > depth + 2.0 * texel + searchRadius * slope) {
            blockerSum += stored;
            blockers++;
        }
    }
    if (blockers == 0) {
        return 1.0;
    }

    float penumbra = clamp((blockerSum / float(blockers) - depth) * lightAngle, 1.5 * texel, searchRadius);
    float reference = depth + 2.0 * texel + penumbra * slope;
    float visibility = 0.0;
    for (int i = 0; i < SHADOW_SAMPLES; i++) {
        vec2 offset = diskSample(i, SHADOW_SAMPLES, rotation) * penumbra;
        visibility += texture(shadowCompareTexture, vec4(uv + offset, layer, reference));
    }
    return visibility / float(SHADOW_SAMPLES);
}

// Looks up what the room's probes saw in a direction, or the studio where there is no
// probe. The ray runs on to the box, so the lookup matches where it really lands.
vec3 sampleReflection(vec3 position, vec3 direction, float lod) {
    vec3 sum = vec3(0.0);
    float covered = 0.0;
    for (int i = 0; i < frame.probeCount && covered < 1.0; i++) {
        vec3 boxMin = frame.probes[i].boxMin.xyz;
        vec3 boxMax = frame.probes[i].boxMax.xyz;
        vec3 margins = min(position - boxMin, boxMax - position);
        float margin = min(margins.x, min(margins.y, margins.z));
        if (margin <= 0.0) {
            continue;
        }

        vec3 exits = max((boxMax - position) / direction, (boxMin - position) / direction);
        vec3 hit = position + direction * min(exits.x, min(exits.y, exits.z));
        vec3 local = hit - frame.probes[i].position.xyz;

        // Faces are stored the way a camera sees them, which mirrors the cube along z.
        vec3 seen = textureLod(probeTexture, vec4(local.x, local.y, -local.z, float(i)), lod).rgb;

        float weight = clamp(margin / max(frame.probes[i].position.w, 1e-3), 0.0, 1.0) * (1.0 - covered);
        sum += seen * weight;
        covered += weight;
    }

    // Probes hold the scene at full strength, but the caller scales for the studio.
    vec3 scene = sum / max(frame.backgroundRadiance.a, 1e-3);
    return scene + textureLod(reflectionTexture, direction, lod).rgb * (1.0 - covered);
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

void main() {
    float depth = texture(depthTexture, inUv).r;

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
            outColor = vec4(textureLod(reflectionTexture, direction, 0.0).rgb * frame.backgroundRadiance.a, 1.0);
            return;
        }

        outColor = vec4(frame.backgroundRadiance.rgb, 1.0);
        return;
    }

    vec4 baseColorMetallic = texture(baseColorMetallicTexture, inUv);
    vec4 normalRoughnessAo = texture(normalRoughnessAoTexture, inUv);

    vec3 baseColor = baseColorMetallic.rgb;
    float metallic = baseColorMetallic.a;
    vec3 n = octahedralDecode(normalRoughnessAo.xy);
    float roughness = clamp(normalRoughnessAo.z, 0.045, 1.0);
    float screenAo = texture(ambientOcclusionTexture, inUv).r;
    float ao = normalRoughnessAo.w * screenAo;
    // Alpha holds the clearcoat, or for foliage how much light shines through as a
    // negative number. writeSurface() in surface.glsl packs it.
    vec4 emissiveClearcoat = texture(emissiveClearcoatTexture, inUv);
    float clearcoat = max(emissiveClearcoat.a, 0.0);
    float translucency = max(-emissiveClearcoat.a, 0.0);

    vec4 clip = vec4(inUv * 2.0 - 1.0, depth, 1.0);
    vec4 world = frame.inverseViewProjection * clip;
    vec3 position = world.xyz / world.w;

    vec3 v = normalize(frame.cameraPosition.xyz - position);
    float nDotV = max(dot(n, v), 1e-4);
    float alpha = roughness * roughness;
    vec3 f0 = mix(vec3(0.04), baseColor, metallic);

    vec2 brdf = environmentBrdf(roughness, nDotV);
    vec3 reflectedLight = sampleReflection(position, reflect(-v, n), roughness * REFLECTION_LODS);

    // Coated surfaces are traced for their mirror-like coat, which then serves both layers.
    float traceStrength = clearcoat > 0.0 ? 1.0 : 1.0 - smoothstep(TRACE_MAX_ROUGHNESS * 0.5, TRACE_MAX_ROUGHNESS, roughness);
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

    reflectedLight = mix(reflectedLight, traced.rgb, traced.a);
    vec3 environmentSpecular = reflectedLight * (f0 * brdf.x + brdf.y);
    vec3 environmentDiffuse = texture(irradianceTexture, n).rgb * baseColor * (1.0 - metallic);
    if (translucency > 0.0) {
        environmentDiffuse += texture(irradianceTexture, -n).rgb * baseColor * (1.0 - metallic) * translucency;
    }

    vec3 environmentLight = environmentDiffuse + environmentSpecular;

    // The coat reflects first and what it lets through lights the layers below.
    vec3 coatFresnel = fresnelSchlick(nDotV, CLEARCOAT_F0) * clearcoat;
    if (clearcoat > 0.0) {
        vec3 coatReflection = sampleReflection(position, reflect(-v, n), CLEARCOAT_ROUGHNESS * REFLECTION_LODS);
        coatReflection = mix(coatReflection, traced.rgb, traced.a);
        environmentLight = environmentLight * (1.0 - coatFresnel) + coatReflection * coatFresnel;
    }

    vec3 color = environmentLight * frame.backgroundRadiance.a * multiBounceOcclusion(ao, baseColor) + emissiveClearcoat.rgb;

    Surface surface = Surface(n, v, nDotV, alpha, f0, baseColor, metallic, clearcoat, translucency);
    for (int i = 0; i < frame.lightCount; i++) {
        vec3 l = frame.lights[i].direction.xyz;
        vec3 lit = shade(surface, l);
        if (lit == vec3(0.0)) {
            continue;
        }

        // A light without a shadow map would shine through everything, so nearby
        // surfaces are all that can block it. Light from behind is looked up from the
        // side it arrives on.
        vec3 lightSide = dot(n, l) >= 0.0 ? n : -n;
        float visibility = frame.lights[i].direction.w > 0.5 ? shadowVisibility(position, lightSide, l) : screenAo;
        color += lit * sunlightReaching(position, l, i) * visibility;
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

        vec3 lit = shade(surface, l);
        if (lit == vec3(0.0)) {
            continue;
        }

        vec3 lightSide = dot(n, l) >= 0.0 ? n : -n;
        float visibility = light.radianceShadow.w >= 0.0 ? localShadow(light, position, lightSide, l) : screenAo;
        color += lit * light.radianceShadow.rgb * reaching * visibility;
    }

    if (isUnderwater()) {
        outColor = vec4(throughWater(color, position), 1.0);
        return;
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
            behind = textureLod(reflectionTexture, normalize(away), 0.0).rgb * frame.backgroundRadiance.a;
        }

        color = mix(color, behind, smoothstep(fadeStart, fadeEnd, hazeDistance(position)));
    }

    outColor = vec4(color, 1.0);
}
