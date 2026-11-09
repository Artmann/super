#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#define EXPOSURE_SET 2
#define EXPOSURE_ACCESS readonly
#include "exposure.glsl"

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D hdrTexture;
layout(set = 1, binding = 1) uniform sampler2D bloomTexture;

layout(location = 0) out vec4 outColor;

// Contrast turns around this mid grey, so overall brightness holds still.
const float MIDDLE_GREY = 0.18;

// ACES expects its input about this much brighter than our lights produce. Middle
// grey would otherwise come out far too dark.
const float ACES_EXPOSURE = 1.0 / 0.6;

// Into and out of the colour space the ACES curve works in, written column by column.
const mat3 ACES_INPUT = mat3(
    0.59719, 0.07600, 0.02840,
    0.35458, 0.90834, 0.13383,
    0.04823, 0.01566, 0.83777);
const mat3 ACES_OUTPUT = mat3(
    1.60475, -0.10208, -0.00327,
    -0.53108, 1.10813, -0.07276,
    -0.07367, -0.00605, 1.07602);

// Stephen Hill's fit of the ACES film transform. Working in the ACES colour space lets
// very bright colours fade towards white without shifting hue.
vec3 acesFilmic(vec3 color) {
    vec3 x = ACES_INPUT * color * ACES_EXPOSURE;
    x = (x * (x + 0.0245786) - 0.000090537) / (x * (0.983729 * x + 0.4329510) + 0.238081);
    return clamp(ACES_OUTPUT * x, 0.0, 1.0);
}

void main() {
    // Every pixel gives a little light to its surroundings, so bright things glow by
    // themselves and no brightness threshold is needed.
    // Left out, the bloom pass never ran, so its target holds nothing to read.
    vec3 color = texture(hdrTexture, inUv).rgb;
    if (!isOff(OFF_BLOOM)) {
        color = mix(color, texture(bloomTexture, inUv).rgb, frame.lift.a);
    }

    // Auto exposure puts the metered brightness at middle grey, and grade.x becomes compensation
    // on top. Left out, the fixed exposure is all there is, exactly as before.
    float exposureScale = frame.grade.x;
    if (!isOff(OFF_AUTO_EXPOSURE)) {
        exposureScale *= MIDDLE_GREY * exp2(-exposure.adaptedEv);
    }

    color = max(color * exposureScale, vec3(0.0));
    color = pow(color / MIDDLE_GREY + 1e-5, vec3(frame.grade.y)) * MIDDLE_GREY;
    color = max(mix(vec3(dot(color, vec3(0.2126, 0.7152, 0.0722))), color, frame.grade.z), vec3(0.0));

    // Gain scales every channel, so it shows most in the highlights. Lift adds colour that
    // fades out towards white, so the shade takes a tint while white stays white.
    color = acesFilmic(color) * frame.gain.rgb;
    color = clamp(color + frame.lift.rgb * (1.0 - color), 0.0, 1.0);

    // The target is an sRGB format, so the hardware applies gamma on write.
    outColor = vec4(color, 1.0);
}
