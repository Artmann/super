#version 460
#extension GL_GOOGLE_include_directive : require
// A blade of grass's surface: its colour from root to tip, darker at the root where the blades round it shade it,
// and translucent, so the sun behind it shines through. Writes the G-buffer as writeSurface in surface.glsl does.
#include "common.glsl"
#include "sampling.glsl"
#include "wind.glsl"
#include "grass.glsl"
#include "gbuffer_marks.glsl"

layout(location = 0) in vec3 inNormal;
layout(location = 1) in float inUp;
layout(location = 2) flat in vec2 inLook;
layout(location = 3) in vec3 inClipNow;
layout(location = 4) in vec3 inClipBefore;

layout(location = 0) out vec4 outBaseColorMetallic;
layout(location = 1) out vec4 outNormalRoughnessAo;
layout(location = 2) out vec4 outEmissiveClearcoat;
layout(location = 3) out vec2 outMotion;
layout(location = 4) out vec4 outBaked;
layout(location = 5) out vec4 outShadowMask;

const float GRASS_ROUGHNESS = 0.55;

// How much soft light reaches the root of a blade, among the others, and how far up it the shade reaches.
const float GRASS_ROOT_LIGHT = 0.3;
const float GRASS_ROOT_SHADE = 0.6;

void main() {
    GrassKind kind = grass.kinds[int(inLook.y)];
    vec3 color = mix(kind.base.rgb, kind.tip.rgb, smoothstep(0.0, 1.0, inUp));
    color *= 1.0 + 0.25 * inLook.x;
    float occlusion = mix(GRASS_ROOT_LIGHT, 1.0, smoothstep(0.0, GRASS_ROOT_SHADE, inUp));
    float roughness = GRASS_ROUGHNESS;
    if (isOff(OFF_MATERIALS)) {
        color = PLAIN_COLOR;
        roughness = PLAIN_ROUGHNESS;
        occlusion = 1.0;
    }

    outMotion = inClipBefore.z > 0.0
        ? (inClipBefore.xy / inClipBefore.z - inClipNow.xy / inClipNow.z) * 0.5 * frame.screen.xy
        : vec2(MOTION_OFF_PICTURE);
    outBaseColorMetallic = vec4(clamp(color, 0.0, 1.0), 0.0);
    outNormalRoughnessAo = vec4(octahedralEncode(normalize(inNormal)), roughness, occlusion);
    outEmissiveClearcoat = vec4(0.0, 0.0, 0.0, foliageChannel(kind.base.a));
    outBaked = vec4(0.0);
    outShadowMask = vec4(1.0);
}
