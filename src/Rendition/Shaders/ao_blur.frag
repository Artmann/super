#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "sampling.glsl"
#include "screen_trace.glsl"

layout(set = 1, binding = 0) uniform sampler2D occlusionTexture;
layout(set = 1, binding = 1) uniform sampler2D depthTexture;

layout(push_constant) uniform Blur {
    ivec2 step; // One pixel of the occlusion along the way this pass blurs.
    int scale;  // Screen pixels per pixel of the occlusion, along each axis: 1 or 2.
} blur;

layout(location = 0) out vec4 outOcclusion;

// Pixels of the occlusion each way. At half resolution each is two pixels of the screen, and the lighting spreads
// them once more as it reads them, so fewer cover the same ground.
const int RADIUS = 3;
const int HALF_RADIUS = 1;

// The depth of the screen pixel a pixel of the occlusion was worked out for.
float depthOf(ivec2 cell) {
    ivec2 size = textureSize(depthTexture, 0);
    return texelFetch(depthTexture, blur.scale > 1 ? tracedPixel(cell, size) : cell, 0).r;
}

// Averages away the grain of the visibility and the bent normal together, but not across
// the edges of objects. It runs twice, across and then down, which reads 14 pixels where
// a square of the same width reads 49. The octahedral form folds over where a direction
// turns away from the camera, so averaging it there gives a direction none of the taps had.
// Each tap's bent normal is decoded and the directions summed instead, which averages
// fairly anywhere.
void main() {
    ivec2 own = ivec2(gl_FragCoord.xy);
    float center = depthOf(own);
    if (center == 0.0) {
        // Nothing was drawn here, so nothing reads the occlusion: what the AO pass writes for the sky.
        outOcclusion = vec4(0.5, 0.5, 1.0, 1.0);
        return;
    }

    ivec2 last = textureSize(occlusionTexture, 0) - 1;
    vec3 direction = vec3(0.0);
    vec2 rest = vec2(0.0);
    float weights = 0.0;
    int radius = blur.scale > 1 ? HALF_RADIUS : RADIUS;
    for (int i = -radius; i <= radius; i++) {
        ivec2 cell = clamp(own + i * blur.step, ivec2(0), last);
        float difference = abs(depthOf(cell) / center - 1.0);
        float weight = exp(-difference * 20.0);
        vec4 tap = texelFetch(occlusionTexture, cell, 0);
        direction += octahedralDecode(tap.xy * 2.0 - 1.0) * weight;
        rest += tap.zw * weight;
        weights += weight;
    }

    // The centre tap always counts fully, so the sum is never zero unless the taps cancel.
    vec3 bent = dot(direction, direction) > 1e-12
        ? normalize(direction)
        : octahedralDecode(texelFetch(occlusionTexture, own, 0).xy * 2.0 - 1.0);
    outOcclusion = vec4(octahedralEncode(bent) * 0.5 + 0.5, rest / weights);
}
