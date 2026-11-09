#version 460
#extension GL_EXT_nonuniform_qualifier : require
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "brdf.glsl"
#include "material.glsl"
#include "light_data.glsl"
#include "reflection_probes.glsl"
#include "sky.glsl"
#include "fog.glsl"

layout(location = 0) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec3 inPosition;

layout(set = 2, binding = 0) uniform samplerCube reflectionTexture;
layout(set = 2, binding = 1) uniform samplerCube irradianceTexture;
layout(set = 2, binding = 2) uniform sampler2D sceneColor; // The picture behind, with blurrier mips.
layout(set = 2, binding = 3) uniform sampler2D sceneDepth;
layout(set = 2, binding = 4) uniform samplerCubeArray probeTexture;

layout(location = 0) out vec4 outColor;

// How far bent light is followed before it's looked up, in metres. Further bends more
// but more often misses what's really behind.
const float REFRACTION_REACH = 0.5;
// How many mips blurrier the scene behind looks per unit of roughness.
const float FROST_LODS = 8.0;

// Where on screen the light reaching this pixel through the glass comes from. It's bent
// by the IOR and followed a short way, but never to something in front of the glass.
vec2 refractedUv(vec2 here, vec3 v, vec3 n, float ior) {
    if (ior <= 1.0001) {
        return here;
    }

    vec3 bent = refract(-v, n, 1.0 / ior);
    float reach = REFRACTION_REACH;
    float behind = texture(sceneDepth, here).r;
    if (behind > 0.0) {
        vec4 world = frame.inverseViewProjection * vec4(here * 2.0 - 1.0, behind, 1.0);
        reach = min(reach, distance(world.xyz / world.w, inPosition));
    }

    vec4 clip = frame.viewProjection * vec4(inPosition + bent * reach, 1.0);
    if (clip.w <= 1e-5) {
        return here;
    }

    vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) {
        return here;
    }

    // Reversed-Z: nearer is larger.
    if (texture(sceneDepth, uv).r > gl_FragCoord.z) {
        return here;
    }

    return uv;
}

// Glass reflects its surroundings at full strength however clear it is. Only its own
// colour is scaled by opacity. When it sees the scene through the copy (draw.tint.w), it
// adds what's behind, bent and blurred, and covers the pixel. Otherwise the output is
// premultiplied by alpha and blends over what's there.
void main() {
    Material material = materials[draw.material];
    vec4 baseColor = texture(textures[material.baseColorTexture], inUv, frame.mipBias) * material.baseColor * vec4(draw.tint.rgb, 1.0);
    // Opacity can come from either texture, as in surface.glsl and shadow.frag: a material
    // graph keeps it in the surface texture's alpha.
    baseColor.a *= texture(textures[material.surfaceTexture], inUv, frame.mipBias).a;
    vec3 emissive = texture(textures[material.emissiveTexture], inUv, frame.mipBias).rgb * material.emissive.rgb;
    float roughnessOfSurface = material.roughness;

    // A see-through ghost is one matte colour that glows a little, like solid ghosts in
    // surface.glsl, with the world showing through as much as its opacity says.
    if (draw.ghost.a > 0.0) {
        vec3 color = clamp(draw.ghost.rgb, 0.0, 1.0);
        baseColor = vec4(color, draw.ghost.a);
        emissive = color * GHOST_GLOW * draw.ghost.a;
        roughnessOfSurface = 0.7;
    }

    // With materials off, see-through things keep how see-through they are, and only that:
    // plain clay in colour, matte, and clear, with no bending or frosting of what's behind.
    bool plain = isOff(OFF_MATERIALS) && draw.ghost.a <= 0.0;
    if (plain) {
        baseColor.rgb = PLAIN_COLOR;
        roughnessOfSurface = PLAIN_ROUGHNESS;
    }

    vec3 n = normalize(gl_FrontFacing ? inNormal : -inNormal);
    vec3 v = normalize(frame.cameraPosition.xyz - inPosition);
    float nDotV = max(dot(n, v), 1e-4);
    float roughness = clamp(roughnessOfSurface, 0.045, 1.0);
    float alpha = roughness * roughness;
    vec3 f0 = vec3(0.04);

    vec2 brdf = environmentBrdf(roughness, nDotV);
    vec3 multiscatter = isOff(OFF_MULTISCATTER_SPECULAR) ? vec3(1.0) : multiscatterCompensation(f0, nDotV, roughness);
    // Glass reflects the yard or room round it where a reflection probe sees it, as lit surfaces do.
    vec3 reflectedLight = probeReflection(probeTexture, reflectionTexture, inPosition, reflect(-v, n), roughness * REFLECTION_LODS);
    vec3 specular = reflectedLight * (f0 * brdf.x + brdf.y) * frame.backgroundRadiance.a * multiscatter;
    vec3 diffuse = texture(irradianceTexture, n).rgb * frame.backgroundRadiance.a;

    for (int i = 0; i < frame.lightCount; i++) {
        vec3 l = frame.lights[i].direction.xyz;
        float nDotL = max(dot(n, l), 0.0);
        if (nDotL == 0.0) {
            continue;
        }

        vec3 h = normalize(v + l);
        vec3 f = fresnelSchlick(max(dot(v, h), 0.0), f0);
        float highlight = distributionGgx(max(dot(n, h), 0.0), alpha) * visibilitySmithGgx(nDotV, nDotL, alpha);
        specular += highlight * f * multiscatter * frame.lights[i].radiance.rgb * nDotL;
        diffuse += frame.lights[i].radiance.rgb * nDotL / PI;
    }

    // Point and spot lights from this pixel's cluster, without their shadows.
    uint cluster = clusterAt(gl_FragCoord.xy, inPosition);
    uint localCount = clusterLightCount(cluster);
    for (uint i = 0; i < localCount; i++) {
        LocalLight light = localLights[clusterLight(cluster, i)];
        vec3 l;
        float reaching = localLightReaching(light, inPosition, l);
        float nDotL = max(dot(n, l), 0.0);
        if (reaching <= 0.0 || nDotL == 0.0) {
            continue;
        }

        vec3 radiance = light.radianceShadow.rgb * reaching;
        vec3 h = normalize(v + l);
        vec3 f = fresnelSchlick(max(dot(v, h), 0.0), f0);
        float highlight = distributionGgx(max(dot(n, h), 0.0), alpha) * visibilitySmithGgx(nDotV, nDotL, alpha);
        specular += highlight * f * multiscatter * radiance * nDotL;
        diffuse += radiance * nDotL / PI;
    }

    float opacity = baseColor.a;
    float reflectance = dot(fresnelSchlick(nDotV, f0), vec3(1.0 / 3.0));
    float coverage = opacity + (1.0 - opacity) * reflectance;
    vec3 own = baseColor.rgb * diffuse * opacity + specular + emissive;

    // The fog in front dims the glass's own light and adds its own over the part the glass
    // covers. What shows through already has the fog all the way to it.
    vec4 fog = fogInFront(inPosition, reflectionTexture);
    if (fog.a < 1.0) {
        own = own * fog.a + fog.rgb * coverage;
    }

    if (draw.tint.w < 0.5) {
        outColor = vec4(own, coverage);
        return;
    }

    vec2 here = gl_FragCoord.xy / vec2(textureSize(sceneDepth, 0));
    vec2 uv = plain ? here : refractedUv(here, v, n, material.ior);
    float lod = plain ? 0.0 : min(roughnessOfSurface * FROST_LODS, float(textureQueryLevels(sceneColor) - 1));
    vec3 transmitted = textureLod(sceneColor, uv, lod).rgb;
    outColor = vec4(own + (1.0 - coverage) * transmitted, 1.0);
}
