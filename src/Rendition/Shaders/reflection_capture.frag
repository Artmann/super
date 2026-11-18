#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : require

// The capture binds the light probes' atlases, its own sun shadow map and the environment's irradiance in a set of its own.
#define PROBE_ATLAS_SET 2

#include "common.glsl"
#include "sampling.glsl"
#include "brdf.glsl"
#include "light_data.glsl"
#include "local_shadows.glsl"
#include "probes.glsl"
#include "water_absorption.glsl"
#include "material_data.glsl"

// Shades what a reflection probe sees, the six faces at once, as probe_capture.vert places them. Each surface is lit as
// the lighting pass lights it, without the shine: the suns and the point and spot lights from probe_light.glsl, then
// the soft light, from a bake's probes, the live light probes or the sky, as soft_light.glsl gives it. A metal shows
// its colour in the soft light round it. Alpha is 1, and the sky's 0 is left for reflection_sky.comp to fill.
// See Passes/ReflectionCapturePass.cs.

layout(location = 0) in vec3 inNormal;
layout(location = 1) in vec2 inUv;
layout(location = 2) in vec3 inPosition;

layout(location = 0) out vec4 outColor;

// Must match probe_capture.vert.
layout(push_constant) uniform Draw {
    vec4 tint;
    vec4 ghost;
    int material;
    int cascade;
    int firstBone;
    float windBase;
    float windSway;
    vec4 probe;
} draw;

layout(set = 1, binding = 0) uniform sampler2D textures[];
layout(std430, set = 1, binding = 1) readonly buffer Materials {
    Material materials[];
};

// The sun's shadow round the probe's box. See Passes/ReflectionCapturePass.cs. Reversed-Z, so a blocker nearer the sun
// stores more.
layout(set = PROBE_ATLAS_SET, binding = 14) uniform sampler2DShadow captureSunShadow;
layout(set = PROBE_ATLAS_SET, binding = 15) uniform samplerCube irradianceTexture;

#include "soft_light.glsl"

// How far a point moves off its surface before its shadows are looked up, so it doesn't shadow itself.
const float CAPTURE_SHADOW_LIFT = 0.05;

float captureSunVisibility(vec3 position, vec3 normal) {
    // A zero matrix means there is no map this frame: the sun lights everything.
    if (frame.reflectionSunMatrix[3][3] == 0.0) {
        return 1.0;
    }

    vec4 projected = frame.reflectionSunMatrix * vec4(position + normal * CAPTURE_SHADOW_LIFT, 1.0);
    vec2 uv = projected.xy * 0.5 + 0.5;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) {
        return 1.0;
    }

    // Reversed-Z: lit where nothing nearer the sun was stored, so the reference is lifted a little towards it.
    return texture(captureSunShadow, vec3(uv, projected.z + 1e-4));
}

#define PROBE_SUN_VISIBILITY(p, n) captureSunVisibility(p, n)
#define PROBE_LIGHT_VISIBILITY(i, p, n) localShadowInAir(localLights[i], (p) + (n) * CAPTURE_SHADOW_LIFT)
#include "probe_light.glsl"

void main() {
    Material material = materials[draw.material];
    vec4 base = texture(textures[material.baseColorTexture], inUv);
    float opacity = base.a * material.baseColor.a * texture(textures[material.surfaceTexture], inUv).a;
    if (opacity < material.alphaCutoff) {
        discard;
    }

    // One-sided surfaces are culled from behind, so only a two-sided one is seen from the back.
    vec3 normal = normalize(gl_FrontFacing ? inNormal : -inNormal);

    vec3 color;
    float metallic;
    if (isOff(OFF_MATERIALS)) {
        color = PLAIN_COLOR;
        metallic = 0.0;
    } else {
        color = base.rgb * material.baseColor.rgb * draw.tint.rgb;
        metallic = material.metallic;
    }

    vec3 toProbe = normalize(draw.probe.xyz - inPosition);
    vec3 soft = max(softLight(inPosition, normal, toProbe), vec3(0.0));
    vec3 direct = probeDirectLight(inPosition, normal);
    vec3 emissive = isOff(OFF_EMISSIVE) ? vec3(0.0) : texture(textures[material.emissiveTexture], inUv).rgb * material.emissive.rgb;
    vec3 lit = color * (1.0 - metallic) * (direct / PI + soft) + color * metallic * soft + emissive;
    outColor = vec4(lit, 1.0);
}
