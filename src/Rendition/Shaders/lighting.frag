#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "brdf.glsl"

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D baseColorMetallicTexture;
layout(set = 1, binding = 1) uniform sampler2D normalRoughnessAoTexture;
layout(set = 1, binding = 2) uniform sampler2D depthTexture;
layout(set = 1, binding = 3) uniform sampler2D shadowDepthTexture;
layout(set = 1, binding = 4) uniform sampler2DShadow shadowCompareTexture;
layout(set = 1, binding = 5) uniform samplerCube reflectionTexture;
layout(set = 1, binding = 6) uniform samplerCube irradianceTexture;
layout(set = 1, binding = 7) uniform sampler2D emissiveClearcoatTexture;

layout(location = 0) out vec4 outColor;

const float GOLDEN_ANGLE = 2.39996323;
const int BLOCKER_SAMPLES = 16;
const int SHADOW_SAMPLES = 24;

// The clear lacquer over car paint is very smooth and reflects like glass.
const float CLEARCOAT_ROUGHNESS = 0.06;
const vec3 CLEARCOAT_F0 = vec3(0.04);

vec3 octahedralDecode(vec2 e) {
    vec3 n = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) {
        vec2 signs = vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
        n.xy = (1.0 - abs(n.yx)) * signs;
    }
    return normalize(n);
}


// Scatters samples evenly over a disc. The per-pixel rotation turns banding into fine grain.
vec2 diskSample(int index, int count, float rotation) {
    float radius = sqrt((float(index) + 0.5) / float(count));
    float angle = float(index) * GOLDEN_ANGLE + rotation;
    return radius * vec2(cos(angle), sin(angle));
}

float interleavedGradientNoise(vec2 pixel) {
    return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
}

// Soft shadow that is sharp where an object touches the surface and blurs with
// distance, like a real studio light. Returns 1 for fully lit.
float shadowVisibility(vec3 position, vec3 n, vec3 l) {
    float lightAngle = frame.parameters.z;
    float nDotL = clamp(dot(n, l), 0.05, 1.0);

    vec4 projected = frame.shadowMatrix * vec4(position + n * frame.parameters.w * (1.0 - nDotL), 1.0);
    vec2 uv = projected.xy * 0.5 + 0.5;
    float depth = projected.z;
    if (uv.x <= 0.0 || uv.x >= 1.0 || uv.y <= 0.0 || uv.y >= 1.0) {
        return 1.0;
    }

    // In this projection one unit of depth covers the same distance as one unit of uv,
    // so a slope's bias is simply the sample offset times the slope.
    float slope = min(sqrt(1.0 - nDotL * nDotL) / nDotL, 2.0);
    float texel = 1.0 / float(textureSize(shadowDepthTexture, 0).x);
    float rotation = interleavedGradientNoise(gl_FragCoord.xy) * 2.0 * PI;
    float searchRadius = max(lightAngle * 0.2, 2.0 * texel);

    // Reversed-Z: blockers sit nearer the light, which means a larger depth.
    float blockerSum = 0.0;
    int blockers = 0;
    for (int i = 0; i < BLOCKER_SAMPLES; i++) {
        vec2 offset = diskSample(i, BLOCKER_SAMPLES, rotation) * searchRadius;
        float stored = texture(shadowDepthTexture, uv + offset).r;
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
        visibility += texture(shadowCompareTexture, vec3(uv + offset, reference));
    }
    return visibility / float(SHADOW_SAMPLES);
}

void main() {
    float depth = texture(depthTexture, inUv).r;

    // Reversed-Z: a depth of 0 is the far plane, so nothing was drawn here.
    if (depth == 0.0) {
        outColor = vec4(frame.backgroundRadiance.rgb, 1.0);
        return;
    }

    vec4 baseColorMetallic = texture(baseColorMetallicTexture, inUv);
    vec4 normalRoughnessAo = texture(normalRoughnessAoTexture, inUv);

    vec3 baseColor = baseColorMetallic.rgb;
    float metallic = baseColorMetallic.a;
    vec3 n = octahedralDecode(normalRoughnessAo.xy);
    float roughness = clamp(normalRoughnessAo.z, 0.045, 1.0);
    float ao = normalRoughnessAo.w;
    vec4 emissiveClearcoat = texture(emissiveClearcoatTexture, inUv);
    float clearcoat = emissiveClearcoat.a;

    vec4 clip = vec4(inUv * 2.0 - 1.0, depth, 1.0);
    vec4 world = frame.inverseViewProjection * clip;
    vec3 position = world.xyz / world.w;

    vec3 v = normalize(frame.cameraPosition.xyz - position);
    float nDotV = max(dot(n, v), 1e-4);
    float alpha = roughness * roughness;
    vec3 f0 = mix(vec3(0.04), baseColor, metallic);

    vec2 brdf = environmentBrdf(roughness, nDotV);
    vec3 reflectedLight = textureLod(reflectionTexture, reflect(-v, n), roughness * REFLECTION_LODS).rgb;
    vec3 environmentSpecular = reflectedLight * (f0 * brdf.x + brdf.y);
    vec3 environmentDiffuse = texture(irradianceTexture, n).rgb * baseColor * (1.0 - metallic);
    vec3 environmentLight = environmentDiffuse + environmentSpecular;

    // The coat reflects first and what it lets through lights the layers below.
    float coatAlpha = CLEARCOAT_ROUGHNESS * CLEARCOAT_ROUGHNESS;
    vec3 coatFresnel = fresnelSchlick(nDotV, CLEARCOAT_F0) * clearcoat;
    if (clearcoat > 0.0) {
        vec3 coatReflection = textureLod(reflectionTexture, reflect(-v, n), CLEARCOAT_ROUGHNESS * REFLECTION_LODS).rgb;
        environmentLight = environmentLight * (1.0 - coatFresnel) + coatReflection * coatFresnel;
    }

    vec3 color = environmentLight * frame.backgroundRadiance.a * ao + emissiveClearcoat.rgb;

    for (int i = 0; i < frame.lightCount; i++) {
        vec3 l = frame.lights[i].direction.xyz;
        float nDotL = max(dot(n, l), 0.0);
        if (nDotL == 0.0) {
            continue;
        }

        vec3 h = normalize(v + l);
        vec3 f = fresnelSchlick(max(dot(v, h), 0.0), f0);
        vec3 specular = distributionGgx(max(dot(n, h), 0.0), alpha) * visibilitySmithGgx(nDotV, nDotL, alpha) * f;
        vec3 diffuse = (1.0 - f) * (1.0 - metallic) * baseColor / PI;

        vec3 lit = diffuse + specular;
        if (clearcoat > 0.0) {
            vec3 coatF = fresnelSchlick(max(dot(v, h), 0.0), CLEARCOAT_F0) * clearcoat;
            float coatHighlight = distributionGgx(max(dot(n, h), 0.0), coatAlpha) * visibilitySmithGgx(nDotV, nDotL, coatAlpha);
            lit = lit * (1.0 - coatF) + coatHighlight * coatF;
        }

        float visibility = frame.lights[i].direction.w > 0.5 ? shadowVisibility(position, n, l) : 1.0;
        color += lit * frame.lights[i].radiance.rgb * nDotL * visibility;
    }

    // Distant surfaces melt into the background, which hides the horizon of a studio floor.
    float fadeStart = frame.parameters.x;
    float fadeEnd = frame.parameters.y;
    if (fadeEnd > fadeStart) {
        float distanceToCamera = length(frame.cameraPosition.xyz - position);
        color = mix(color, frame.backgroundRadiance.rgb, smoothstep(fadeStart, fadeEnd, distanceToCamera));
    }

    outColor = vec4(color, 1.0);
}
