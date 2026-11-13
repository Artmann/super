#version 460
#extension GL_GOOGLE_include_directive : require
#include "sampling.glsl"

layout(location = 0) in vec2 inUv;

layout(set = 0, binding = 0) uniform sampler2D occlusionTexture;
layout(set = 0, binding = 1) uniform sampler2D depthTexture;

layout(push_constant) uniform Blur {
    vec2 step; // One pixel along the way this pass blurs, in uv.
} blur;

layout(location = 0) out vec4 outOcclusion;

const int RADIUS = 3;

// Averages away the grain of the visibility and the bent normal together, but not across
// the edges of objects. It runs twice, across and then down, which reads 14 pixels where
// a square of the same width reads 49. The octahedral form folds over where a direction
// turns away from the camera, so averaging it there gives a direction none of the taps had.
// Each tap's bent normal is decoded and the directions summed instead, which averages
// fairly anywhere.
void main() {
    float center = texture(depthTexture, inUv).r;
    if (center == 0.0) {
        // Nothing was drawn here, so nothing reads the occlusion: what the AO pass writes for the sky.
        outOcclusion = vec4(0.5, 0.5, 1.0, 1.0);
        return;
    }

    vec3 direction = vec3(0.0);
    vec2 rest = vec2(0.0);
    float weights = 0.0;
    for (int i = -RADIUS; i <= RADIUS; i++) {
        vec2 uv = inUv + float(i) * blur.step;
        float difference = abs(texture(depthTexture, uv).r / center - 1.0);
        float weight = exp(-difference * 20.0);
        vec4 tap = texture(occlusionTexture, uv);
        direction += octahedralDecode(tap.xy * 2.0 - 1.0) * weight;
        rest += tap.zw * weight;
        weights += weight;
    }

    // The centre tap always counts fully, so the sum is never zero unless the taps cancel.
    vec3 bent = dot(direction, direction) > 1e-12
        ? normalize(direction)
        : octahedralDecode(texture(occlusionTexture, inUv).xy * 2.0 - 1.0);
    outOcclusion = vec4(octahedralEncode(bent) * 0.5 + 0.5, rest / weights);
}
