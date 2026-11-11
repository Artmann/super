#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "dof.glsl"

// The first depth of field pass, at half size: each pixel's colour, the average of the four scene
// pixels under it, and its circle's signed radius in scene pixels, from the nearest of the four. The
// nearest wins so a blurred thing in front keeps its edge rather than losing it to what's behind.

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D sourceTexture;
layout(set = 1, binding = 1) uniform sampler2D depthTexture;

layout(location = 0) out vec4 outColor;

void main() {
    ivec2 size = textureSize(depthTexture, 0);
    ivec2 base = ivec2(gl_FragCoord.xy) * 2;
    float nearest = 0.0;
    ivec2 nearestPixel = base;
    for (int i = 0; i < 4; i++) {
        ivec2 pixel = min(base + ivec2(i & 1, i >> 1), size - 1);
        float depth = texelFetch(depthTexture, pixel, 0).r;
        // Depth runs from 1 near the camera to 0 at the horizon.
        if (depth > nearest) {
            nearest = depth;
            nearestPixel = pixel;
        }
    }

    vec2 uv = (vec2(nearestPixel) + 0.5) / vec2(size);
    outColor = vec4(texture(sourceTexture, inUv).rgb, circleRadius(uv, nearest));
}
