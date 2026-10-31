#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D currentTexture;
layout(set = 1, binding = 1) uniform sampler2D historyTexture;
layout(set = 1, binding = 2) uniform sampler2D depthTexture;

layout(location = 0) out vec4 outColor;

const float HISTORY_WEIGHT = 0.92;

// How far history may stray from this frame's nearby pixels, in standard deviations.
const float HISTORY_TOLERANCE = 1.25;

// Averaging raw HDR lets one very bright pixel dominate, so edges next to bright
// surfaces stay jagged. Blending in this squeezed range keeps them smooth.
vec3 squeeze(vec3 color) {
    return color / (1.0 + max(color.r, max(color.g, color.b)));
}

vec3 unsqueeze(vec3 color) {
    return color / max(1.0 - max(color.r, max(color.g, color.b)), 1e-3);
}

// Catmull-Rom lookup. Plain blending would soften the picture a little every frame.
vec3 sampleHistory(vec2 uv) {
    vec2 size = vec2(textureSize(historyTexture, 0));
    vec2 position = uv * size - 0.5;
    vec2 base = floor(position);
    vec2 f = position - base;

    vec2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    vec2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    vec2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    vec2 w3 = f * f * (-0.5 + 0.5 * f);
    vec2 w12 = w1 + w2;

    vec2 uv0 = (base - 0.5) / size;
    vec2 uv3 = (base + 2.5) / size;
    vec2 uv12 = (base + 0.5 + w2 / w12) / size;

    vec3 color = texture(historyTexture, vec2(uv12.x, uv0.y)).rgb * w12.x * w0.y
        + texture(historyTexture, vec2(uv0.x, uv12.y)).rgb * w0.x * w12.y
        + texture(historyTexture, uv12).rgb * w12.x * w12.y
        + texture(historyTexture, vec2(uv3.x, uv12.y)).rgb * w3.x * w12.y
        + texture(historyTexture, vec2(uv12.x, uv3.y)).rgb * w12.x * w3.y;
    float total = w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y;
    return max(color / total, vec3(0.0));
}

// Temporal anti-aliasing: the camera shifts by less than a pixel each frame, so blending
// with where this surface was on screen last frame averages many samples per pixel.
void main() {
    vec3 current = texture(currentTexture, inUv).rgb;
    if (frame.historyValid == 0) {
        outColor = vec4(current, 1.0);
        return;
    }

    // Takes in the nearest surface around the pixel, so the edges of objects follow them.
    vec2 texel = 1.0 / vec2(textureSize(currentTexture, 0));
    vec3 mean = vec3(0.0);
    vec3 meanSquared = vec3(0.0);
    float nearest = 0.0;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            vec2 uv = inUv + vec2(x, y) * texel;
            vec3 color = squeeze(texture(currentTexture, uv).rgb);
            mean += color;
            meanSquared += color * color;
            nearest = max(nearest, texture(depthTexture, uv).r); // Reversed-Z: larger is nearer.
        }
    }
    mean /= 9.0;
    meanSquared /= 9.0;

    // History has no shift of its own, so this frame's shift comes out of the lookup.
    // Left homogeneous, because the background sits at infinity.
    vec4 world = frame.inverseViewProjection * vec4(inUv * 2.0 - 1.0 + frame.finish.zw, nearest, 1.0);
    vec4 previous = frame.previousViewProjection * world;
    vec2 historyUv = previous.xy / previous.w * 0.5 + 0.5;
    if (previous.w <= 0.0 || any(lessThan(historyUv, vec2(0.0))) || any(greaterThan(historyUv, vec2(1.0)))) {
        outColor = vec4(current, 1.0);
        return;
    }

    // History showing something this frame doesn't, such as a surface that moved away,
    // falls outside the range of nearby colours and gets pulled back in.
    vec3 spread = sqrt(max(meanSquared - mean * mean, vec3(0.0))) * HISTORY_TOLERANCE;
    vec3 history = clamp(squeeze(sampleHistory(historyUv)), mean - spread, mean + spread);

    outColor = vec4(unsqueeze(mix(squeeze(current), history, HISTORY_WEIGHT)), 1.0);
}
