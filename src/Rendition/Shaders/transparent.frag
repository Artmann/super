#version 460
#extension GL_EXT_nonuniform_qualifier : require
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "brdf.glsl"
#include "material.glsl"

layout(location = 0) in vec3 inNormal;
layout(location = 2) in vec2 inUv;
layout(location = 3) in vec3 inPosition;

layout(set = 2, binding = 0) uniform samplerCube reflectionTexture;
layout(set = 2, binding = 1) uniform samplerCube irradianceTexture;

layout(location = 0) out vec4 outColor;

// Glass reflects its surroundings at full strength however clear it is. Only the
// tint is scaled by opacity, so the output is premultiplied by alpha.
void main() {
    Material material = materials[draw.material];
    vec4 baseColor = texture(textures[material.baseColorTexture], inUv) * material.baseColor;
    vec3 emissive = texture(textures[material.emissiveTexture], inUv).rgb * material.emissive.rgb;

    vec3 n = normalize(gl_FrontFacing ? inNormal : -inNormal);
    vec3 v = normalize(frame.cameraPosition.xyz - inPosition);
    float nDotV = max(dot(n, v), 1e-4);
    float roughness = clamp(material.roughness, 0.045, 1.0);
    float alpha = roughness * roughness;
    vec3 f0 = vec3(0.04);

    vec2 brdf = environmentBrdf(roughness, nDotV);
    vec3 reflectedLight = textureLod(reflectionTexture, reflect(-v, n), roughness * REFLECTION_LODS).rgb;
    vec3 specular = reflectedLight * (f0 * brdf.x + brdf.y) * frame.backgroundRadiance.a;
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
        specular += highlight * f * frame.lights[i].radiance.rgb * nDotL;
        diffuse += frame.lights[i].radiance.rgb * nDotL / PI;
    }

    float opacity = baseColor.a;
    float reflectance = dot(fresnelSchlick(nDotV, f0), vec3(1.0 / 3.0));
    float coverage = opacity + (1.0 - opacity) * reflectance;
    outColor = vec4(baseColor.rgb * diffuse * opacity + specular + emissive, coverage);
}
