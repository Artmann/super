#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "brdf.glsl"
#include "sampling.glsl"

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D depthTexture;
layout(set = 1, binding = 1) uniform sampler2D normalRoughnessAoTexture;

layout(location = 0) out vec4 outOcclusion;

const float RADIUS = 0.6; // Metres.
const float STRENGTH = 0.9;
const float BIAS = 0.02;
const int SAMPLES = 24;

vec3 worldPosition(vec2 uv, float depth) {
    vec4 world = frame.inverseViewProjection * vec4(uv * 2.0 - 1.0, depth, 1.0);
    return world.xyz / world.w;
}

// Alchemy ambient obscurance (McGuire et al.): nearby surfaces that rise above this
// point's tangent plane hide part of the sky from it.
void main() {
    float depth = texture(depthTexture, inUv).r;
    if (depth == 0.0) {
        outOcclusion = vec4(1.0);
        return;
    }

    vec3 position = worldPosition(inUv, depth);
    vec3 n = octahedralDecode(texture(normalRoughnessAoTexture, inUv).xy);

    // How big the radius looks on screen at this distance.
    vec3 view = normalize(frame.cameraPosition.xyz - position);
    vec3 side = normalize(cross(view, vec3(0.01, 1.0, 0.0)));
    vec4 edge = frame.viewProjection * vec4(position + side * RADIUS, 1.0);
    vec2 size = vec2(textureSize(depthTexture, 0));
    float screenRadius = min(length((edge.xy / edge.w * 0.5 + 0.5 - inUv) * size), size.y * 0.25);

    float rotation = interleavedGradientNoise(gl_FragCoord.xy) * 2.0 * PI;
    float radiusSquared = RADIUS * RADIUS;
    float sum = 0.0;
    for (int i = 0; i < SAMPLES; i++) {
        vec2 uv = inUv + diskSample(i, SAMPLES, rotation) * screenRadius / size;
        float sampleDepth = texture(depthTexture, uv).r;
        if (sampleDepth == 0.0) {
            continue;
        }

        vec3 toSample = worldPosition(uv, sampleDepth) - position;
        float distanceSquared = dot(toSample, toSample);
        float falloff = max(radiusSquared - distanceSquared, 0.0);
        sum += falloff * falloff * falloff * max((dot(toSample, n) - BIAS) / (distanceSquared + 0.001), 0.0);
    }

    float scale = STRENGTH * 5.0 / (radiusSquared * radiusSquared * radiusSquared * float(SAMPLES));
    outOcclusion = vec4(max(1.0 - sum * scale, 0.0));
}
