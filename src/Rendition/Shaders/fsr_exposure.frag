#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#define EXPOSURE_SET 1
#define EXPOSURE_ACCESS readonly
#include "exposure.glsl"

// The exposure FSR scales the picture by when it judges colours, the same one tonemap.frag will use. The meter
// runs after FSR, so this is last frame's adapted exposure, which moves too slowly for the difference to show.

layout(location = 0) out float outExposure;

const float MIDDLE_GREY = 0.18;

void main() {
    float exposureScale = frame.grade.x;
    if (!isOff(OFF_AUTO_EXPOSURE)) {
        exposureScale *= MIDDLE_GREY * exp2(-exposure.adaptedEv);
    }

    outExposure = exposureScale;
}
