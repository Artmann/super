#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "brdf.glsl"
#include "sampling.glsl"
#include "material_data.glsl"
#include "screen_trace.glsl"

// Screen-space reflections, traced before the lighting, which reads the result. At half resolution each pixel here
// stands for a block of four on screen and traces from one of them; at full resolution it traces from its own.
// rgb is the colour the ray found and a how far to trust it, or -1 where the surface isn't one that is traced.

layout(set = 1, binding = 0) uniform sampler2D depthTexture;
layout(set = 1, binding = 1) uniform sampler2D normalRoughnessAoTexture;
layout(set = 1, binding = 2) uniform sampler2D emissiveClearcoatTexture;
layout(set = 1, binding = 3) uniform sampler2D historyTexture;

layout(push_constant) uniform Trace {
    int scale; // Screen pixels per pixel of the target, along each axis: 1 or 2.
} trace;

layout(location = 0) out vec4 outTraced;

// How reflected rays walk through the depth buffer. Steps grow with distance, because
// far away precision matters less. Metres, except for the growth.
const int TRACE_STEPS = 56;
const int TRACE_REFINE_STEPS = 5;
const float TRACE_FIRST_STEP = 0.02;
const float TRACE_GROWTH = 1.07;
const float TRACE_DISTANCE = 12.0;
const float TRACE_THICKNESS = 0.25;
const float TRACE_MAX_RADIANCE = 16.0;

const vec4 NOT_TRACED = vec4(0.0, 0.0, 0.0, -1.0);

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

// Walks along the ray until it passes behind something the camera sees, then reads that spot from the previous
// finished frame. rgb is the colour and a is how far to trust it, since rays leaving the screen know nothing.
vec4 traceReflection(vec3 position, vec3 n, vec3 direction, float noise) {
    vec3 start = position + n * 0.01;
    // A point's clip position is linear along the ray, so each step is one multiply and add, not a matrix.
    vec4 clipStart = frame.viewProjection * vec4(start, 1.0);
    vec4 clipAlong = frame.viewProjection * vec4(direction, 0.0);
    float stepLength = TRACE_FIRST_STEP;
    float last = 0.0;
    float travelled = stepLength * (0.5 + noise);
    for (int i = 0; i < TRACE_STEPS && travelled < TRACE_DISTANCE; i++) {
        vec4 clip = clipStart + clipAlong * travelled;
        if (clip.w <= 0.0) {
            break;
        }

        vec2 uv;
        float gap = depthGap(depthTexture, clip, uv);
        if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
            break;
        }

        if (gap > 0.0 && gap < max(TRACE_THICKNESS, stepLength * 2.0)) {
            float nearer = last;
            float farther = travelled;
            for (int j = 0; j < TRACE_REFINE_STEPS; j++) {
                float middle = 0.5 * (nearer + farther);
                if (depthGap(depthTexture, clipStart + clipAlong * middle, uv) > 0.0) {
                    farther = middle;
                } else {
                    nearer = middle;
                }
            }

            // Hitting the back of a surface means the ray slipped through the depth buffer.
            depthGap(depthTexture, clipStart + clipAlong * farther, uv);
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
    ivec2 size = textureSize(depthTexture, 0);
    ivec2 cell = ivec2(gl_FragCoord.xy);
    ivec2 pixel = trace.scale > 1 ? tracedPixel(cell, size) : cell;

    // Reversed-Z: a depth of 0 is the far plane, so nothing was drawn here.
    float depth = texelFetch(depthTexture, pixel, 0).r;
    if (depth == 0.0) {
        outTraced = NOT_TRACED;
        return;
    }

    // The same unpacking as lighting.frag, which must agree on what is traced.
    vec4 normalRoughnessAo = texelFetch(normalRoughnessAoTexture, pixel, 0);
    float roughness = clamp(normalRoughnessAo.z, 0.045, 1.0);
    float coat = texelFetch(emissiveClearcoatTexture, pixel, 0).a;
    float clearcoat = coat >= SUBSURFACE_MARK - 0.5 ? 0.0 : max(coat, 0.0);
    if (traceStrength(roughness, clearcoat) <= 0.0) {
        outTraced = NOT_TRACED;
        return;
    }

    vec2 centre = vec2(pixel) + 0.5;
    vec4 clip = vec4(centre / vec2(size) * 2.0 - 1.0, depth, 1.0);
    vec4 world = frame.inverseViewProjection * clip;
    vec3 position = world.xyz / world.w;
    if (normalRoughnessAo.w > 0.5 * (1.0 + WATER_SIDE)) {
        position = onWaterSurface(clip, position);
    }

    vec3 n = octahedralDecode(normalRoughnessAo.xy);
    vec3 v = normalize(frame.cameraPosition.xyz - position);

    // Two independent numbers per pixel, each visiting new values every frame.
    vec2 random = fract(vec2(
        interleavedGradientNoise(centre) + float(frame.frameNumber % 64) * 0.7548777,
        interleavedGradientNoise(centre.yx + 47.0) + float(frame.frameNumber % 64) * 0.5698403));
    float scatter = clearcoat > 0.0 ? CLEARCOAT_ROUGHNESS * CLEARCOAT_ROUGHNESS : roughness * roughness;
    outTraced = traceReflection(position, n, scatterReflection(v, n, scatter, random), random.x);
}
