#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "brdf.glsl"
#include "sampling.glsl"
#include "screen_trace.glsl"

layout(set = 1, binding = 0) uniform sampler2D depthTexture;
layout(set = 1, binding = 1) uniform sampler2D normalRoughnessAoTexture;
layout(set = 1, binding = 2) uniform sampler2DArray shadowDepthTexture;

layout(push_constant) uniform Occlusion {
    int scale; // Screen pixels per pixel of the target, along each axis: 1 or 2.
} occlusion;

// xy: the bent normal in view space, octahedral and moved into 0 to 1; z: visibility.
layout(location = 0) out vec4 outOcclusion;

const int SLICES = 2;
const int STEPS = 8; // Each way along a slice.

// Surfaces fade out of the search over the last part of the radius, so the occlusion
// doesn't stop at a hard edge.
const float FALLOFF_START = 0.6;

// Share of the shadow map to search around a point, and how close a blocker can be
// before it counts as the surface itself.
const float OVERHEAD_RADIUS = 0.08;
const float OVERHEAD_BIAS = 0.008;
const float OVERHEAD_STRENGTH = 0.75;
const int OVERHEAD_SAMPLES = 8;

vec3 worldPosition(vec2 uv, float depth) {
    vec4 world = frame.inverseViewProjection * vec4(uv * 2.0 - 1.0, depth, 1.0);
    return world.xyz / world.w;
}

vec3 viewPosition(vec2 uv, float depth) {
    return (frame.view * vec4(worldPosition(uv, depth), 1.0)).xyz;
}

// The camera can't see the underside of a car, but the light can. The shadow map's
// first cascade shows how much stands over a point, which dims the soft light
// reaching the ground.
float overheadOcclusion(vec3 position, vec3 n, float rotation) {
    vec4 projected = frame.shadowMatrices[0] * vec4(position + n * frame.parameters.w, 1.0);
    vec2 uv = projected.xy * 0.5 + 0.5;
    if (projected.z <= 0.0 || projected.z >= 1.0) {
        return 0.0;
    }

    // The light rarely looks straight down, so a surface's depth changes across the map.
    // Following its slope stops a flat floor from counting as its own blocker.
    vec3 facing = mat3(frame.shadowMatrices[0]) * n;
    vec2 slope = -facing.xy * 0.5 / (sign(facing.z) * max(abs(facing.z), 0.02));

    float blocked = 0.0;
    for (int i = 0; i < OVERHEAD_SAMPLES; i++) {
        vec2 offset = diskSample(i, OVERHEAD_SAMPLES, rotation) * OVERHEAD_RADIUS;
        vec2 sampleUv = uv + offset;
        bool insideMap = all(greaterThan(sampleUv, vec2(0.0))) && all(lessThan(sampleUv, vec2(1.0)));

        // The map holds 0 where nothing was drawn. Near its far limit the surface's expected
        // depth can dip below that, and empty space must not count as a blocker.
        float surfaceDepth = max(projected.z + dot(offset, slope) + OVERHEAD_BIAS * frame.shadowDepthScales.x, 0.0);
        if (insideMap && texture(shadowDepthTexture, vec3(sampleUv, 0.0)).r > surfaceDepth) {
            blocked += 1.0;
        }
    }
    return blocked / float(OVERHEAD_SAMPLES) * max(n.y, 0.0);
}

// Ground-truth ambient occlusion (Jimenez et al., "Practical Real-Time Strategies for
// Accurate Indirect Occlusion", 2016). The hemisphere above a point is cut into slices
// that face the camera. Along each, the highest surface within the radius on either side
// is its horizon, and the arc between the two horizons, weighted by the cosine to the
// normal, is how much of the sky's light reaches the point. The same arcs give the bent
// normal: the average direction the point still sees the sky in.
//
// At half resolution each pixel here stands for a block of four on screen and works out the occlusion of one of
// them, a different one each frame, exactly as it would at full resolution.
void main() {
    vec2 size = vec2(textureSize(depthTexture, 0));
    ivec2 cell = ivec2(gl_FragCoord.xy);
    ivec2 pixel = occlusion.scale > 1 ? tracedPixel(cell, ivec2(size)) : cell;
    vec2 inUv = (vec2(pixel) + 0.5) / size;

    float depth = texelFetch(depthTexture, pixel, 0).r;
    if (depth == 0.0) {
        outOcclusion = vec4(0.5, 0.5, 1.0, 1.0);
        return;
    }

    float radius = frame.occlusion.x;
    float strength = frame.occlusion.y;

    vec3 world = worldPosition(inUv, depth);
    vec3 worldNormal = octahedralDecode(texelFetch(normalRoughnessAoTexture, pixel, 0).xy);
    vec3 position = (frame.view * vec4(world, 1.0)).xyz;
    vec3 n = normalize(mat3(frame.view) * worldNormal);
    vec3 v = normalize(-position);

    // How big the radius looks on screen at this distance.
    vec3 toCamera = normalize(frame.cameraPosition.xyz - world);
    vec3 side = normalize(cross(toCamera, vec3(0.01, 1.0, 0.0)));
    vec4 edge = frame.viewProjection * vec4(world + side * radius, 1.0);
    float screenRadius = min(length((edge.xy / edge.w * 0.5 + 0.5 - inUv) * size), size.y * 0.25);

    // One number turns the slices, the other shifts the steps along them.
    float noise = frameNoise(gl_FragCoord.xy, frame.frameNumber);
    float stepNoise = fract(interleavedGradientNoise(gl_FragCoord.yx + 47.0) + float(frame.frameNumber % 64) * 0.7548777);
    float falloffRange = max((1.0 - FALLOFF_START) * radius, 1e-4);

    float visibility = 0.0;
    vec3 bentNormal = vec3(0.0);
    for (int slice = 0; slice < SLICES; slice++) {
        float angle = (float(slice) + noise) / float(SLICES) * PI;
        vec2 omega = vec2(cos(angle), sin(angle));

        // The slice's direction in view space, found by stepping across the screen at this
        // depth, so it follows whichever way the screen's axes point.
        vec3 along = viewPosition(inUv + omega / size, depth) - position;
        vec3 direction = normalize(along - dot(along, v) * v);
        vec3 axis = cross(direction, v);
        vec3 projectedNormal = n - axis * dot(n, axis);
        float projectedLength = length(projectedNormal);
        if (projectedLength < 1e-4) {
            continue;
        }

        // Angles are measured from the view vector, positive towards the slice's direction.
        float cosN = clamp(dot(projectedNormal, v) / projectedLength, -1.0, 1.0);
        float normalAngle = (dot(projectedNormal, direction) < 0.0 ? -1.0 : 1.0) * acos(cosN);

        // x: the side towards the direction, y: the side away. With nothing in the way, a
        // horizon lies along the surface.
        vec2 lowest = vec2(cos(normalAngle + 0.5 * PI), cos(normalAngle - 0.5 * PI));
        vec2 horizonCos = lowest;
        for (int s = 0; s < STEPS; s++) {
            // Steps bunch up near the point, where small creases matter most, but each
            // moves at least a pixel further.
            float t = (float(s) + stepNoise) / float(STEPS);
            vec2 offset = omega * max(t * t * screenRadius, float(s) + 1.0) / size;
            for (int sideIndex = 0; sideIndex < 2; sideIndex++) {
                vec2 uv = sideIndex == 0 ? inUv + offset : inUv - offset;
                if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) {
                    continue;
                }

                float sampleDepth = texture(depthTexture, uv).r;
                if (sampleDepth == 0.0) {
                    continue;
                }

                vec3 delta = viewPosition(uv, sampleDepth) - position;
                float distance = length(delta);
                float sampleCos = dot(delta, v) / max(distance, 1e-5);
                float weight = clamp((radius - distance) / falloffRange, 0.0, 1.0);
                horizonCos[sideIndex] = max(horizonCos[sideIndex], mix(lowest[sideIndex], sampleCos, weight));
            }
        }

        // Neither horizon can be further than a right angle from the normal.
        float h1 = normalAngle + min(acos(horizonCos.x) - normalAngle, 0.5 * PI);
        float h0 = normalAngle + max(-acos(horizonCos.y) - normalAngle, -0.5 * PI);

        float sinN = sin(normalAngle);
        float arc0 = (cosN + 2.0 * h0 * sinN - cos(2.0 * h0 - normalAngle)) * 0.25;
        float arc1 = (cosN + 2.0 * h1 * sinN - cos(2.0 * h1 - normalAngle)) * 0.25;
        visibility += projectedLength * (arc0 + arc1);

        // The cosine-weighted average direction across the visible arc, within the slice.
        float t0 = (6.0 * sin(h0 - normalAngle) - sin(3.0 * h0 - normalAngle)
            + 6.0 * sin(h1 - normalAngle) - sin(3.0 * h1 - normalAngle)
            + 16.0 * sinN - 3.0 * (sin(h0 + normalAngle) + sin(h1 + normalAngle))) / 12.0;
        float t1 = (-cos(3.0 * h0 - normalAngle) - cos(3.0 * h1 - normalAngle)
            + 8.0 * cosN - 3.0 * (cos(h0 + normalAngle) + cos(h1 + normalAngle))) / 12.0;
        bentNormal += (direction * t0 + v * t1) * projectedLength;
    }

    visibility = clamp(visibility / float(SLICES), 0.0, 1.0);
    bentNormal = dot(bentNormal, bentNormal) > 1e-8 ? normalize(bentNormal) : n;

    visibility = mix(1.0, visibility, strength);
    visibility *= 1.0 - OVERHEAD_STRENGTH * overheadOcclusion(world, worldNormal, noise * 2.0 * PI);
    outOcclusion = vec4(octahedralEncode(bentNormal) * 0.5 + 0.5, visibility, 1.0);
}
