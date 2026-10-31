#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "sampling.glsl"

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D sourceTexture;

layout(location = 0) out vec4 outColor;

const float DITHER_STRENGTH = 1.0 / 255.0;

// The last touches: restores crispness, darkens the corners and adds fine noise. An
// 8-bit screen can't show a slow gradient smoothly, and the noise hides those steps.
void main() {
    vec2 texel = 1.0 / vec2(textureSize(sourceTexture, 0));
    vec3 center = texture(sourceTexture, inUv).rgb;
    vec3 north = texture(sourceTexture, inUv + vec2(0.0, -texel.y)).rgb;
    vec3 south = texture(sourceTexture, inUv + vec2(0.0, texel.y)).rgb;
    vec3 east = texture(sourceTexture, inUv + vec2(texel.x, 0.0)).rgb;
    vec3 west = texture(sourceTexture, inUv + vec2(-texel.x, 0.0)).rgb;

    // Staying within the neighbours' range avoids bright halos along edges.
    vec3 low = min(center, min(min(north, south), min(east, west)));
    vec3 high = max(center, max(max(north, south), max(east, west)));
    vec3 color = clamp(center + (center - (north + south + east + west) * 0.25) * frame.finish.x, low, high);

    vec2 fromCenter = inUv - 0.5;
    color *= 1.0 - frame.grade.w * smoothstep(0.1, 0.5, dot(fromCenter, fromCenter));

    // Noise belongs in the same steps as the screen, which aren't linear in brightness.
    float noise = frameNoise(gl_FragCoord.xy, frame.frameNumber) - 0.5;
    vec3 display = max(sqrt(color) + noise * (DITHER_STRENGTH + frame.finish.y), vec3(0.0));
    outColor = vec4(display * display, 1.0);
}
