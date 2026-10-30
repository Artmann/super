#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D baseColorMetallicTexture;
layout(set = 1, binding = 1) uniform sampler2D normalRoughnessAoTexture;
layout(set = 1, binding = 2) uniform sampler2D depthTexture;

layout(location = 0) out vec4 outColor;

const float PI = 3.14159265359;

vec3 octahedralDecode(vec2 e) {
    vec3 n = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) {
        vec2 signs = vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
        n.xy = (1.0 - abs(n.yx)) * signs;
    }
    return normalize(n);
}

float distributionGgx(float nDotH, float alpha) {
    float a2 = alpha * alpha;
    float d = nDotH * nDotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d);
}

float visibilitySmithGgx(float nDotV, float nDotL, float alpha) {
    float a2 = alpha * alpha;
    float v = nDotL * sqrt(nDotV * nDotV * (1.0 - a2) + a2);
    float l = nDotV * sqrt(nDotL * nDotL * (1.0 - a2) + a2);
    return 0.5 / max(v + l, 1e-5);
}

vec3 fresnelSchlick(float vDotH, vec3 f0) {
    return f0 + (1.0 - f0) * pow(1.0 - vDotH, 5.0);
}

void main() {
    float depth = texture(depthTexture, inUv).r;

    // Reversed-Z: a depth of 0 is the far plane, so nothing was drawn here.
    if (depth == 0.0) {
        outColor = vec4(mix(vec3(0.02, 0.025, 0.035), vec3(0.09, 0.11, 0.15), 1.0 - inUv.y), 1.0);
        return;
    }

    vec4 baseColorMetallic = texture(baseColorMetallicTexture, inUv);
    vec4 normalRoughnessAo = texture(normalRoughnessAoTexture, inUv);

    vec3 baseColor = baseColorMetallic.rgb;
    float metallic = baseColorMetallic.a;
    vec3 n = octahedralDecode(normalRoughnessAo.xy);
    float roughness = clamp(normalRoughnessAo.z, 0.045, 1.0);
    float ao = normalRoughnessAo.w;

    vec4 clip = vec4(inUv * 2.0 - 1.0, depth, 1.0);
    vec4 world = frame.inverseViewProjection * clip;
    vec3 position = world.xyz / world.w;

    vec3 v = normalize(frame.cameraPosition.xyz - position);
    vec3 l = frame.lightDirection.xyz;
    vec3 h = normalize(v + l);

    float nDotL = max(dot(n, l), 0.0);
    float nDotV = max(dot(n, v), 1e-4);
    float nDotH = max(dot(n, h), 0.0);
    float vDotH = max(dot(v, h), 0.0);

    float alpha = roughness * roughness;
    vec3 f0 = mix(vec3(0.04), baseColor, metallic);
    vec3 f = fresnelSchlick(vDotH, f0);

    vec3 specular = distributionGgx(nDotH, alpha) * visibilitySmithGgx(nDotV, nDotL, alpha) * f;
    vec3 diffuse = (1.0 - f) * (1.0 - metallic) * baseColor / PI;

    vec3 ambient = frame.ambientRadiance.rgb * mix(baseColor, f0, metallic) * ao;
    outColor = vec4((diffuse + specular) * frame.lightRadiance.rgb * nDotL + ambient, 1.0);
}
