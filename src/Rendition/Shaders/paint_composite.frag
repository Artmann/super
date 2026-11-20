#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "paint.glsl"

// Lays the half-size painted picture over the full-size one, as much as each pixel's paint says: the whole amount, or
// faded in with distance. Below PAINT_CUT the pixel is discarded, so it stays exactly as it was. Premultiplied: the
// picture keeps 1 - alpha of itself.

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D paintedTexture;
layout(set = 1, binding = 1) uniform sampler2D depthTexture;

layout(location = 0) out vec4 outColor;

void main() {
    float share = paintMask(inUv, texture(depthTexture, inUv).r);
    if (share < PAINT_CUT) {
        discard;
    }

    outColor = vec4(texture(paintedTexture, inUv).rgb * share, share);
}
