#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "brdf.glsl"
#include "sampling.glsl"

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D depthTexture;
layout(set = 1, binding = 1) uniform sampler2D normalRoughnessAoTexture;
layout(set = 1, binding = 2) uniform sampler2D shadowDepthTexture;

layout(location = 0) out vec4 outOcclusion;

const float RADIUS = 0.6; // Metres.
const float STRENGTH = 0.9;
const float BIAS = 0.02;
const int SAMPLES = 24;

// Share of the shadow map to search around a point, and how close a blocker can be
// before it counts as the surface itself.
const float OVERHEAD_RADIUS = 0.08;
const float OVERHEAD_BIAS = 0.008;
const float OVERHEAD_STRENGTH = 0.75;
const int OVERHEAD_SAMPLES = 16;

vec3 worldPosition(vec2 uv, float depth) {
    vec4 world = frame.inverseViewProjection * vec4(uv * 2.0 - 1.0, depth, 1.0);
    return world.xyz / world.w;
}

// The camera can't see the underside of a car, but the light can. The shadow map
// shows how much stands over a point, which dims the soft light reaching the ground.
float overheadOcclusion(vec3 position, vec3 n, float rotation) {
    vec4 projected = frame.shadowMatrix * vec4(position + n * frame.parameters.w, 1.0);
    vec2 uv = projected.xy * 0.5 + 0.5;

    // The light rarely looks straight down, so a surface's depth changes across the map.
    // Following its slope stops a flat floor from counting as its own blocker.
    vec3 facing = mat3(frame.shadowMatrix) * n;
    vec2 slope = -facing.xy * 0.5 / (sign(facing.z) * max(abs(facing.z), 0.02));

    float blocked = 0.0;
    for (int i = 0; i < OVERHEAD_SAMPLES; i++) {
        vec2 offset = diskSample(i, OVERHEAD_SAMPLES, rotation) * OVERHEAD_RADIUS;
        vec2 sampleUv = uv + offset;
        bool insideMap = all(greaterThan(sampleUv, vec2(0.0))) && all(lessThan(sampleUv, vec2(1.0)));
        if (insideMap && texture(shadowDepthTexture, sampleUv).r > projected.z + dot(offset, slope) + OVERHEAD_BIAS) {
            blocked += 1.0;
        }
    }
    return blocked / float(OVERHEAD_SAMPLES) * max(n.y, 0.0);
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

    float rotation = frameNoise(gl_FragCoord.xy, frame.frameNumber) * 2.0 * PI;
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
    float occlusion = max(1.0 - sum * scale, 0.0);
    outOcclusion = vec4(occlusion * (1.0 - OVERHEAD_STRENGTH * overheadOcclusion(position, n, rotation)));
}
