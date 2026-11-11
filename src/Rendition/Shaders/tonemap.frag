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

// AgX, as Blender 4 uses with its default look, in Benjamin Wrensch's minimal form. The inset
// matrix pulls every colour a little towards white before the curve, so a bright, strongly
// coloured light fades towards white instead of clipping to one flat hue. Written column by column.
const mat3 AGX_INSET = mat3(
    0.842479062253094, 0.0423282422610123, 0.0423756549057051,
    0.0784335999999992, 0.878468636469772, 0.0784336,
    0.0792237451477643, 0.0791661274605434, 0.879142973793104);
const mat3 AGX_OUTSET = mat3(
    1.19687900512017, -0.0528968517574562, -0.0529716355144438,
    -0.0980208811401368, 1.15190312990417, -0.0980434501171241,
    -0.0990297440797205, -0.0989611768448433, 1.15107367264116);

// The log2 range the curve spans: about 10 stops below middle grey and 6.5 above.
const float AGX_MIN_EV = -12.47393;
const float AGX_MAX_EV = 4.026069;

// A polynomial fit of AgX's default contrast curve, over log-encoded 0 to 1.
vec3 agxContrast(vec3 x) {
    vec3 x2 = x * x;
    vec3 x4 = x2 * x2;
    return 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
}

// look.x is a power and look.y a saturation, worked on the curve's display values the way
// Blender's looks are: Punchy is 1.35 and 1.4, the base look 1 and 1.
vec3 agx(vec3 color, vec2 look) {
    vec3 x = AGX_INSET * color;
    x = clamp(log2(max(x, vec3(1e-10))), AGX_MIN_EV, AGX_MAX_EV);
    x = max(agxContrast((x - AGX_MIN_EV) / (AGX_MAX_EV - AGX_MIN_EV)), vec3(0.0));
    float luma = dot(x, vec3(0.2126, 0.7152, 0.0722));
    x = luma + look.y * (pow(x, vec3(look.x)) - luma);
    // The curve gives display values for a 2.2 gamma screen; the target wants them linear.
    x = pow(clamp(AGX_OUTSET * x, 0.0, 1.0), vec3(2.2));
    return x;
}

void main() {
    // Every pixel gives a little light to its surroundings, so bright things glow by
    // themselves and no brightness threshold is needed.
    // Left out, the bloom pass never ran, so its target holds nothing to read.
    vec3 color = texture(hdrTexture, inUv).rgb;
    // A debug view's colours are shown as they are: no exposure, grade or curve.
    if (isOff(OFF_DEBUG_VIEW)) {
        outColor = vec4(clamp(color, 0.0, 1.0), 1.0);
        return;
    }

    if (!isOff(OFF_BLOOM)) {
        color = mix(color, texture(bloomTexture, inUv).rgb, frame.lift.a);
    }

    // Auto exposure puts the metered brightness at middle grey, and grade.x becomes compensation
    // on top. Left out, the fixed exposure is all there is, exactly as before.
    float exposureScale = frame.grade.x;
    if (!isOff(OFF_AUTO_EXPOSURE)) {
        exposureScale *= MIDDLE_GREY * exp2(-exposure.adaptedEv);
    }

    // White balance comes first, on the light itself, as a camera's would.
    color = max(mat3(frame.whiteBalance) * (color * exposureScale), vec3(0.0));
    color = pow(color / MIDDLE_GREY + 1e-5, vec3(frame.grade.y)) * MIDDLE_GREY;
    color = max(mix(vec3(dot(color, vec3(0.2126, 0.7152, 0.0722))), color, frame.grade.z), vec3(0.0));

    // Gain scales every channel, so it shows most in the highlights. Lift adds colour that
    // fades out towards white, so the shade takes a tint while white stays white.
    // curve.x picks the curve: 1 for ACES, 0 for AgX. It is the same for every pixel.
    color = (frame.curve.x > 0.5 ? acesFilmic(color) : agx(color, frame.curve.yz)) * frame.gain.rgb;
    color = clamp(color + frame.lift.rgb * (1.0 - color), 0.0, 1.0);

    // The target is an sRGB format, so the hardware applies gamma on write.
    outColor = vec4(color, 1.0);
}
