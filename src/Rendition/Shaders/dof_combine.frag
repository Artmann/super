#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "dof.glsl"

// The last depth of field pass, at full size, blended over the picture. The far layer shows where
// this pixel's own circle, worked out again here at full size for crisp edges, says it's out of
// focus behind. The near layer goes over that wherever it covers, taken from the strongest of five
// taps two scene pixels apart, so a blurred thing in front spreads a little past its own edge.

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D nearTexture;
layout(set = 1, binding = 1) uniform sampler2D depthTexture;
layout(set = 1, binding = 2) uniform sampler2D farTexture;

layout(location = 0) out vec4 outColor;

void main() {
    vec2 scenePixel = 1.0 / vec2(textureSize(depthTexture, 0));
    float radius = circleRadius(inUv, texture(depthTexture, inUv).r);

    vec4 farTap = texture(farTexture, inUv);
    float farAlpha = farTap.a > 0.0 ? layerShare(radius) : 0.0;
    vec3 farColor = farTap.a > 0.0 ? farTap.rgb / farTap.a : vec3(0.0);

    vec4 nearTap = texture(nearTexture, inUv);
    const vec2 SPREAD[4] = vec2[](vec2(2.0, 0.0), vec2(-2.0, 0.0), vec2(0.0, 2.0), vec2(0.0, -2.0));
    for (int i = 0; i < 4; i++) {
        vec4 tap = texture(nearTexture, inUv + SPREAD[i] * scenePixel);
        if (tap.a > nearTap.a) {
            nearTap = tap;
        }
    }

    float nearAlpha = nearTap.a;
    vec3 nearColor = nearAlpha > 0.0 ? nearTap.rgb / nearAlpha : vec3(0.0);

    // Premultiplied: the picture keeps 1 - alpha of itself.
    float alpha = 1.0 - (1.0 - farAlpha) * (1.0 - nearAlpha);
    outColor = vec4(farColor * farAlpha * (1.0 - nearAlpha) + nearColor * nearAlpha, alpha);
}
