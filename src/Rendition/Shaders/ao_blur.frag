#version 460
#extension GL_GOOGLE_include_directive : require
#include "sampling.glsl"

layout(location = 0) in vec2 inUv;

layout(set = 0, binding = 0) uniform sampler2D occlusionTexture;
layout(set = 0, binding = 1) uniform sampler2D depthTexture;

layout(location = 0) out vec4 outOcclusion;

const int RADIUS = 3;

// Averages away the grain of the visibility and the bent normal together, but not across
// the edges of objects. The octahedral form folds over where a direction turns away from
// the camera, so averaging it there gives a direction none of the taps had. Each tap's
// bent normal is decoded and the directions summed instead, which averages fairly anywhere.
void main() {
    vec2 texel = 1.0 / vec2(textureSize(occlusionTexture, 0));
    float center = texture(depthTexture, inUv).r;
    vec3 direction = vec3(0.0);
    vec2 rest = vec2(0.0);
    float weights = 0.0;
    for (int y = -RADIUS; y <= RADIUS; y++) {
        for (int x = -RADIUS; x <= RADIUS; x++) {
            vec2 uv = inUv + vec2(x, y) * texel;
            float difference = abs(texture(depthTexture, uv).r / max(center, 1e-6) - 1.0);
            float weight = exp(-difference * 20.0);
            vec4 tap = texture(occlusionTexture, uv);
            direction += octahedralDecode(tap.xy * 2.0 - 1.0) * weight;
            rest += tap.zw * weight;
            weights += weight;
        }
    }

    // The centre tap always counts fully, so the sum is never zero unless the taps cancel.
    vec3 bent = dot(direction, direction) > 1e-12
        ? normalize(direction)
        : octahedralDecode(texture(occlusionTexture, inUv).xy * 2.0 - 1.0);
    outOcclusion = vec4(octahedralEncode(bent) * 0.5 + 0.5, rest / weights);
}
