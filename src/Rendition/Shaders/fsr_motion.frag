#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"

// FSR's motion vectors: the scene's own, in pixels from now back to then, with the camera's motion filled in where
// nothing was drawn. The sky writes no motion, but it still slides across the screen as the camera turns, and FSR
// would smear it if told it stood still. Worked out the way taa.frag works out the camera's part.

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D depthTexture;
layout(set = 1, binding = 1) uniform sampler2D motionTexture; // see Passes/Motion.cs

layout(location = 0) out vec2 outMotion;

// Far enough that FSR finds no history for the pixel, for a direction that was behind the camera last frame.
const float OFF_PICTURE = -60000.0;

void main() {
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    float depth = texelFetch(depthTexture, pixel, 0).r;
    if (depth > 0.0) {
        outMotion = texelFetch(motionTexture, pixel, 0).xy;
        return;
    }

    // Depth 0 is the horizon at infinity, so this is a direction, and only the camera's turn moves it.
    vec2 ndc = inUv * 2.0 - 1.0;
    vec4 surface = frame.previousViewProjection * (frame.inverseViewProjection * vec4(ndc, 0.0, 1.0));
    outMotion = surface.w > 0.0
        ? (surface.xy / surface.w - (ndc - frame.finish.zw)) * 0.5 * frame.screen.xy
        : vec2(OFF_PICTURE);
}
