#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "sampling.glsl"

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D currentTexture;
layout(set = 1, binding = 1) uniform sampler2D historyTexture;
layout(set = 1, binding = 2) uniform sampler2D depthTexture;
layout(set = 1, binding = 3) uniform sampler2D motionTexture; // see Passes/Motion.cs

layout(location = 0) out vec4 outColor;

const float HISTORY_WEIGHT = 0.92;

// How far history may stray from this frame's nearby pixels, in standard deviations.
const float HISTORY_TOLERANCE = 1.25;

// How far, in pixels, a surface's own motion may differ from the camera's before it counts as moving by itself: a
// hundredth of a pixel, plus what 16-bit floats lose of a large motion.
const float MOTION_TOLERANCE = 0.01;
const float MOTION_PRECISION = 1.0 / 512.0;

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
    return catmullRom(historyTexture, uv);
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
    vec2 nearestUv = inUv;
    for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) {
            vec2 uv = inUv + vec2(x, y) * texel;
            vec3 color = squeeze(texture(currentTexture, uv).rgb);
            mean += color;
            meanSquared += color * color;
            float depth = texture(depthTexture, uv).r;
            if (depth > nearest) { // Reversed-Z: larger is nearer.
                nearest = depth;
                nearestUv = uv;
            }
        }
    }
    mean /= 9.0;
    meanSquared /= 9.0;

    // History has no shift of its own, so this frame's shift comes out of the lookup.
    // Left homogeneous, because the background sits at infinity.
    vec4 world = frame.inverseViewProjection * vec4(inUv * 2.0 - 1.0 + frame.finish.zw, nearest, 1.0);
    vec4 previous = frame.previousViewProjection * world;
    vec2 historyUv = previous.xy / previous.w * 0.5 + 0.5;
    bool behind = previous.w <= 0.0;

    // A surface that moved by itself, such as a walking figure or grass in the wind, follows its motion vector,
    // from the nearest surface around too. Where the motion is the camera's alone, worked out here for the same
    // pixel, the camera's reprojection above stands, so a still scene gives what it always has. The sky wrote no
    // motion, and is left to the camera.
    if (nearest > 0.0) {
        vec2 motion = texture(motionTexture, nearestUv).xy;
        vec2 ndc = nearestUv * 2.0 - 1.0;
        vec4 surface = frame.previousViewProjection * (frame.inverseViewProjection * vec4(ndc, nearest, 1.0));
        vec2 cameraMotion = (surface.xy / surface.w - (ndc - frame.finish.zw)) * 0.5 * frame.screen.xy;
        if (surface.w > 0.0 && length(motion - cameraMotion) > MOTION_TOLERANCE + MOTION_PRECISION * length(cameraMotion)) {
            historyUv = inUv + motion / frame.screen.xy;
            behind = false;
        }
    }

    // Written so a motion that isn't a number falls back too.
    if (behind || !(all(greaterThanEqual(historyUv, vec2(0.0))) && all(lessThanEqual(historyUv, vec2(1.0))))) {
        outColor = vec4(current, 1.0);
        return;
    }

    // History showing something this frame doesn't, such as a surface that moved away,
    // falls outside the range of nearby colours and gets pulled back in.
    vec3 spread = sqrt(max(meanSquared - mean * mean, vec3(0.0))) * HISTORY_TOLERANCE;
    vec3 history = clamp(squeeze(sampleHistory(historyUv)), mean - spread, mean + spread);

    outColor = vec4(unsqueeze(mix(squeeze(current), history, HISTORY_WEIGHT)), 1.0);
}
