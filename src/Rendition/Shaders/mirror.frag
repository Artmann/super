#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : require

// The mirror binds the light probes' atlases, the sun's cascades and the environment's irradiance in a set of its own.
#define PROBE_ATLAS_SET 2

#include "common.glsl"
#include "sampling.glsl"
#include "brdf.glsl"
#include "light_data.glsl"
#include "local_shadows.glsl"
#include "probes.glsl"
#include "water_absorption.glsl"
#include "material_data.glsl"

// Shades what the planar reflection's mirrored camera sees, as mirror.vert places it. Each surface is lit as a
// reflection probe's capture lights it, without the shine: the suns, through the frame's own cascades, and the point
// and spot lights from probe_light.glsl, then the soft light from soft_light.glsl. The first target takes the colour
// with a coverage of 1, the second how high the surface stands over the plane, which the lighting pass blurs the
// reflection by; both are 0 where nothing was drawn. See PlanarStage.cs.

layout(location = 0) in vec3 inNormal;
layout(location = 1) in vec2 inUv;
layout(location = 2) in vec3 inPosition;

layout(location = 0) out vec4 outColor;
layout(location = 1) out vec4 outHeight;

// Must match DrawConstants in Passes/SceneDraws.cs.
layout(push_constant) uniform Draw {
    vec4 tint;
    vec4 ghost;
    int material;
    int cascade;
    int firstBone;
    float windBase;
    float windSway;
} draw;

layout(set = 1, binding = 0) uniform sampler2D textures[];
layout(std430, set = 1, binding = 1) readonly buffer Materials {
    Material materials[];
};

// The sun's cascades, as the lighting pass reads them. Reversed-Z, so a blocker nearer the sun stores more.
layout(set = PROBE_ATLAS_SET, binding = 14) uniform sampler2DArrayShadow mirrorSunShadow;
layout(set = PROBE_ATLAS_SET, binding = 15) uniform samplerCube irradianceTexture;

#include "soft_light.glsl"

// How far a point moves off its surface before its shadows are looked up, so it doesn't shadow itself.
const float MIRROR_SHADOW_LIFT = 0.05;

// The sun's light at a point, from the first cascade whose map holds it, in one comparison: a reflection is seen
// blurred and from afar, so its shadows needn't be soft.
float mirrorSunVisibility(vec3 position, vec3 normal) {
    vec3 lifted = position + normal * MIRROR_SHADOW_LIFT;
    for (int c = 0; c < frame.shadowCascades; c++) {
        vec4 projected = frame.shadowMatrices[c] * vec4(lifted, 1.0);
        vec2 uv = projected.xy * 0.5 + 0.5;
        if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))) || projected.z <= 0.0 || projected.z >= 1.0) {
            continue;
        }

        // Reversed-Z: lit where nothing nearer the sun was stored, so the reference is lifted a little towards it.
        float texel = 1.0 / float(textureSize(mirrorSunShadow, 0).x);
        float bias = 2.0 * texel * frame.shadowDepthScales[c];
        return texture(mirrorSunShadow, vec4(uv, float(c), projected.z + bias));
    }

    return 1.0;
}

#define PROBE_SUN_VISIBILITY(p, n) mirrorSunVisibility(p, n)
#define PROBE_LIGHT_VISIBILITY(i, p, n) localShadowInAir(localLights[i], (p) + (n) * MIRROR_SHADOW_LIFT)
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

    // Light reaches the real camera by way of the plane, leaving the point towards the mirrored eye.
    vec3 toCamera = normalize(frame.mirrorEye.xyz - inPosition);
    vec3 soft = max(softLight(inPosition, normal, toCamera), vec3(0.0));
    vec3 direct = probeDirectLight(inPosition, normal);
    vec3 emissive = isOff(OFF_EMISSIVE) ? vec3(0.0) : texture(textures[material.emissiveTexture], inUv).rgb * material.emissive.rgb;
    vec3 lit = color * (1.0 - metallic) * (direct / PI + soft) + color * metallic * soft + emissive;
    outColor = vec4(lit, 1.0);
    outHeight = vec4(max(inPosition.y - frame.mirrorPlane.x, 0.0), 0.0, 0.0, 1.0);
}
