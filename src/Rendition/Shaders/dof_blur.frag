#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "dof.glsl"

// The second depth of field pass, at half size. It gathers two layers over discs of 48 taps:
//
// The far layer is what's behind the focus, blurred by this pixel's own circle. Only taps that are
// behind the focus themselves count, each weighted by whether its circle reaches here, so a sharp
// thing in front never smears into the blurred background.
//
// The near layer is what's in front of the focus, spread by each tap's circle over whatever is
// behind it, the way a branch close to the lens veils the scene. Each tap counts for the share of
// its circle's area that one tap stands for, so a field of equal circles covers a pixel exactly once,
// and the sum is how much the near layer covers here.
//
// Both are written premultiplied, so the bilinear upsample in dof_combine.frag mixes them fairly.

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D circleTexture;

layout(location = 0) out vec4 outNear;
layout(location = 1) out vec4 outFar;

const int TAPS = 48;
const float GOLDEN_ANGLE = 2.39996323;

// Tap i of a disc spread evenly by the golden angle, at radius 1 for the whole disc.
vec2 discTap(int i) {
    float radius = sqrt((float(i) + 0.5) / float(TAPS));
    float angle = float(i) * GOLDEN_ANGLE;
    return radius * vec2(cos(angle), sin(angle));
}

void main() {
    vec2 texel = 1.0 / vec2(textureSize(circleTexture, 0));
    vec4 center = texture(circleTexture, inUv);

    // In half-size pixels from here on.
    float widest = lens.maxRadius * 0.5;
    float centerRadius = center.a * 0.5;

    // How far the near layer must look: the widest near circle that reaches here.
    float nearReach = max(-centerRadius, 0.0);
    for (int i = 0; i < TAPS; i++) {
        vec2 offset = discTap(i) * widest;
        float tapNear = -texture(circleTexture, inUv + offset * texel).a * 0.5;
        if (tapNear + 0.5 >= length(offset)) {
            nearReach = max(nearReach, tapNear);
        }
    }

    vec3 nearSum = vec3(0.0);
    float nearCover = 0.0;
    if (nearReach > 0.0) {
        for (int i = 0; i < TAPS; i++) {
            vec2 offset = discTap(i) * nearReach;
            vec4 tap = texture(circleTexture, inUv + offset * texel);
            float tapNear = max(-tap.a * 0.5, 0.0);
            float reaches = clamp(tapNear - length(offset) + 0.5, 0.0, 1.0);
            // One tap stands for nearReach^2 / TAPS of area, and the tap's light is spread over its own circle.
            float share = nearReach * nearReach / (float(TAPS) * max(tapNear * tapNear, 0.0625));
            float weight = reaches * layerShare(tapNear * 2.0) * share;
            nearSum += tap.rgb * weight;
            nearCover += weight;
        }
    }

    float nearAlpha = clamp(nearCover, 0.0, 1.0);
    vec3 nearColor = nearCover > 0.0 ? nearSum / nearCover : vec3(0.0);
    outNear = vec4(nearColor * nearAlpha, nearAlpha);

    // Only a pixel behind the focus has a far layer. The rest write nothing, which the combine sees.
    if (centerRadius <= 0.0) {
        outFar = vec4(0.0);
        return;
    }

    vec3 farSum = center.rgb;
    float farWeight = 1.0;
    if (centerRadius > 0.25) {
        for (int i = 0; i < TAPS; i++) {
            vec2 offset = discTap(i) * centerRadius;
            vec4 tap = texture(circleTexture, inUv + offset * texel);
            float tapFar = max(tap.a * 0.5, 0.0);
            float weight = clamp(tapFar - length(offset) + 0.5, 0.0, 1.0);
            farSum += tap.rgb * weight;
            farWeight += weight;
        }
    }

    outFar = vec4(farSum / farWeight, 1.0);
}
