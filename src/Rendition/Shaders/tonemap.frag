#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "grade_lut.glsl"
#include "sampling.glsl"
#define EXPOSURE_SET 2
#define EXPOSURE_ACCESS readonly
#include "exposure.glsl"

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D hdrTexture;
layout(set = 1, binding = 1) uniform sampler2D bloomTexture;
// The lens flare, half the picture's size. LensFlareDraw clears it when there is none. Binding 3 is left free.
layout(set = 1, binding = 2) uniform sampler2D flareTexture;

// The whole grade, from white balance to the artist's table, baked by grade_lut.comp.
layout(set = 3, binding = 0) uniform sampler3D gradeTable;

layout(location = 0) out vec4 outColor;

// Auto exposure puts the metered brightness here.
const float MIDDLE_GREY = 0.18;

// How far the colour fringes reach at strength 1, in uv per unit of the distance from the middle cubed: a corner's
// red and blue land about 16 pixels either side of its green on a 1080p picture.
const float ABERRATION_REACH = 0.033;

// How many points along its streak the speed blur reads, and how much of the way to its centre the streak reaches at 1.
const int SPEED_BLUR_TAPS = 16;
const float SPEED_BLUR_REACH = 0.25;

// The scene's light with its glow. Every pixel gives a little light to its surroundings, so bright things glow by
// themselves and no brightness threshold is needed. Left out, the bloom pass never ran, so its target holds nothing to read.
// Read at the top level: the speed blur's taps start at noise that jumps from pixel to pixel, and with that in the
// derivatives an implicit level would read a small, averaged level of the targets and wash the picture out.
vec3 glowing(vec2 uv) {
    vec3 color = textureLod(hdrTexture, uv, 0.0).rgb;
    if (!isOff(OFF_BLOOM)) {
        color = mix(color, textureLod(bloomTexture, uv, 0.0).rgb, frame.lift.a);
    }

    return color;
}

// The light at uv through a lens that bends red outwards and blue inwards more the further from the middle it is.
vec3 fringed(vec2 uv, float aberration) {
    if (aberration <= 0.0) {
        return glowing(uv);
    }

    vec2 fromCenter = uv - 0.5;
    vec2 shift = fromCenter * dot(fromCenter, fromCenter) * (aberration * ABERRATION_REACH);
    return vec3(glowing(uv - shift).r, glowing(uv).g, glowing(uv + shift).b);
}

// The light, streaked towards the speed blur's centre, with the colour fringes on each point it reads. The points
// start at a different place each pixel, so the steps between them come out as fine noise rather than copies.
vec3 scene(vec2 uv) {
    float aberration = frame.screenLens.x;
    float blur = frame.screenLens.y;
    if (blur <= 0.0) {
        return fringed(uv, aberration);
    }

    vec2 streak = (frame.screenLens.zw - uv) * (blur * SPEED_BLUR_REACH);
    float start = frameNoise(gl_FragCoord.xy, frame.frameNumber) / float(SPEED_BLUR_TAPS);
    vec3 sum = vec3(0.0);
    for (int tap = 0; tap < SPEED_BLUR_TAPS; tap++) {
        sum += fringed(uv + streak * (start + float(tap) / float(SPEED_BLUR_TAPS)), aberration);
    }

    return sum / float(SPEED_BLUR_TAPS);
}

void main() {
    // A debug view's colours are shown as they are: no glow, screen effects, exposure, grade or curve.
    if (isOff(OFF_DEBUG_VIEW)) {
        outColor = vec4(clamp(texture(hdrTexture, inUv).rgb, 0.0, 1.0), 1.0);
        return;
    }

    // The flare is light in the lens, so the speed blur and the fringes, which move the scene, leave it be.
    vec3 color = scene(inUv) + textureLod(flareTexture, inUv, 0.0).rgb;

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
