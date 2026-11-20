#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "grade_lut.glsl"
#define EXPOSURE_SET 2
#define EXPOSURE_ACCESS readonly
#include "exposure.glsl"

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D hdrTexture;
layout(set = 1, binding = 1) uniform sampler2D bloomTexture;

// The whole grade, from white balance to the artist's table, baked by grade_lut.comp.
layout(set = 3, binding = 0) uniform sampler3D gradeTable;

layout(location = 0) out vec4 outColor;

// Auto exposure puts the metered brightness here.
const float MIDDLE_GREY = 0.18;

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

    // Everything after exposure is the same for every pixel, so it is one look-up.
    vec3 u = gradeLutEncode(color * exposureScale);
    color = texture(gradeTable, lutCoordinate(u, GRADE_LUT_SIZE)).rgb;

    // The target is an sRGB format, so the hardware applies gamma on write.
    outColor = vec4(clamp(color, 0.0, 1.0), 1.0);
}
