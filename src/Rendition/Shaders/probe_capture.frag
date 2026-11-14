#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : require

// The probe updates bind the atlases, and the probes' own sun shadow map, in a set of their own.
#define PROBE_ATLAS_SET 2

#include "common.glsl"
#include "sampling.glsl"
#include "brdf.glsl"
#include "light_data.glsl"
#include "local_shadows.glsl"
#include "probes.glsl"
#include "water_absorption.glsl"
#include "material_data.glsl"

// Shades what a light probe sees: the light each surface sends towards it, from probe_light.glsl,
// in rgb, and the distance to the probe in alpha. The back of a one-sided surface is black at a
// negative distance, which is how probe_fold.comp tells a probe inside a wall. Where nothing is
// drawn, the clear colour marks the sky. See Passes/ProbeCapturePass.cs.

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

// The probes' own sun shadow map, centred on the camera and wide enough for every cascade. See
// Passes/ProbeSunShadow.cs. Reversed-Z, so a blocker nearer the sun stores more.
layout(set = PROBE_ATLAS_SET, binding = 14) uniform sampler2DShadow probeSunShadow;

// How far a point moves off its surface before its shadows are looked up, so it doesn't shadow itself.
const float PROBE_SHADOW_LIFT = 0.05;

float probeSunVisibility(vec3 position, vec3 normal) {
    // A zero matrix means there is no map this frame: the sun lights everything.
    if (frame.probeSunMatrix[3][3] == 0.0) {
        return 1.0;
    }

    vec4 projected = frame.probeSunMatrix * vec4(position + normal * PROBE_SHADOW_LIFT, 1.0);
    vec2 uv = projected.xy * 0.5 + 0.5;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) {
        return 1.0;
    }

    // Reversed-Z: lit where nothing nearer the sun was stored, so the reference is lifted a little towards it.
    return texture(probeSunShadow, vec3(uv, projected.z + 1e-4));
}

#define PROBE_SUN_VISIBILITY(p, n) probeSunVisibility(p, n)
#define PROBE_LIGHT_VISIBILITY(i, p, n) localShadowInAir(localLights[i], (p) + (n) * PROBE_SHADOW_LIFT)
#include "probe_light.glsl"

void main() {
    Material material = materials[draw.material];
    vec4 base = texture(textures[material.baseColorTexture], inUv);
    float opacity = base.a * material.baseColor.a * texture(textures[material.surfaceTexture], inUv).a;
    if (opacity < material.alphaCutoff) {
        discard;
    }

    float distanceToProbe = length(inPosition - draw.probe.xyz);
    vec3 normal = normalize(inNormal);
    if (!gl_FrontFacing) {
        if ((material.flags & MATERIAL_TWO_SIDED) == 0u) {
            // The back of a one-sided surface: the probe is inside something.
            outColor = vec4(0.0, 0.0, 0.0, -distanceToProbe);
            return;
        }

        normal = -normal;
    }

    vec3 albedo;
    if (isOff(OFF_MATERIALS)) {
        albedo = PLAIN_COLOR;
    } else {
        albedo = base.rgb * material.baseColor.rgb * draw.tint.rgb * (1.0 - material.metallic);
    }

    vec3 emissive = texture(textures[material.emissiveTexture], inUv).rgb * material.emissive.rgb;
    outColor = vec4(probeRadiance(inPosition, normal, albedo, emissive), distanceToProbe);
}
