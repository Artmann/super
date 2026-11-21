#version 460
#extension GL_GOOGLE_include_directive : require
// A blade of grass's surface: its colour from root to tip, tinted by its clump and the patch it grows in, darker at the
// root where the blades round it shade it, the more so where they stand thick, and translucent, so the sun behind it
// shines through. Writes the G-buffer as writeSurface in surface.glsl does.
#include "common.glsl"
#include "sampling.glsl"
#include "wind.glsl"
#include "grass.glsl"
#include "gbuffer_marks.glsl"

layout(location = 0) in vec3 inNormal;
layout(location = 1) in float inUp;
layout(location = 2) flat in uvec2 inLook; // x: the blade's tint; y: its look
layout(location = 3) in vec3 inClipNow;
layout(location = 4) in vec3 inClipBefore;

layout(location = 0) out vec4 outBaseColorMetallic;
layout(location = 1) out vec4 outNormalRoughnessAo;
layout(location = 2) out vec4 outEmissiveClearcoat;
layout(location = 3) out vec2 outMotion;
layout(location = 4) out vec4 outBaked;
layout(location = 5) out vec4 outShadowMask;

// A blade's roughness, from the smoothest to the roughest: a little sheen, never a glint.
const vec2 GRASS_ROUGHNESS = vec2(0.5, 0.7);

// How much soft light reaches the root of a blade where the grass is thin, and where it is thick, and how far up it the
// shade reaches.
const vec2 GRASS_ROOT_LIGHT = vec2(0.8, 0.3);
const float GRASS_ROOT_SHADE = 0.5;

// How much darker the root's own colour is where the grass stands thickest.
const float GRASS_ROOT_DARK = 0.2;

// The colour dry grass fades toward, as straw.
const vec3 GRASS_STRAW = vec3(0.36, 0.3, 0.14);

void main() {
    GrassKind kind = grass.kinds[inLook.y];
    // x: brightness; y: yellowing; z: dryness; w: how thick the grass round it stands.
    vec4 tint = unpackUnorm4x8(inLook.x);
    vec3 color = mix(kind.base.rgb, kind.tip.rgb, smoothstep(0.0, 1.0, inUp));
    color = mix(color, color * vec3(1.1, 1.05, 0.65), tint.y);
    color = mix(color, GRASS_STRAW * (0.4 + 0.6 * inUp), tint.z * 0.7);
    color *= mix(0.7, 1.3, tint.x);
    // The tip catches the light; the root is in the shade of the blades round it.
    color *= 1.0 + 0.25 * smoothstep(0.75, 1.0, inUp);
    float rootShade = smoothstep(0.0, GRASS_ROOT_SHADE, inUp);
    color *= mix(1.0 - GRASS_ROOT_DARK * tint.w, 1.0, rootShade);
    float occlusion = mix(mix(GRASS_ROOT_LIGHT.x, GRASS_ROOT_LIGHT.y, tint.w), 1.0, rootShade);
    float roughness = mix(GRASS_ROUGHNESS.x, GRASS_ROUGHNESS.y, tint.x);
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
