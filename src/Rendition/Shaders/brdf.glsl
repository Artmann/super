// How surfaces reflect light. Shared by the lighting and transparent passes.

const float PI = 3.14159265359;
const float REFLECTION_LODS = 5.0; // Must match ReflectionMips in Vulkan/EnvironmentMap.cs.

float distributionGgx(float nDotH, float alpha) {
    float a2 = alpha * alpha;
    float d = nDotH * nDotH * (a2 - 1.0) + 1.0;
    return a2 / (PI * d * d);
}

float visibilitySmithGgx(float nDotV, float nDotL, float alpha) {
    float a2 = alpha * alpha;
    float v = nDotL * sqrt(nDotV * nDotV * (1.0 - a2) + a2);
    float l = nDotV * sqrt(nDotL * nDotL * (1.0 - a2) + a2);
    return 0.5 / max(v + l, 1e-5);
}

vec3 fresnelSchlick(float vDotH, vec3 f0) {
    return f0 + (1.0 - f0) * pow(1.0 - vDotH, 5.0);
}

// How much of the reflected environment reaches the eye, split into a scale and a
// bias on f0. A curve fit by Lazarov and Karis, so no lookup texture is needed.
vec2 environmentBrdf(float roughness, float nDotV) {
    vec4 r = roughness * vec4(-1.0, -0.0275, -0.572, 0.022) + vec4(1.0, 0.0425, 1.04, -0.04);
    float a = min(r.x * r.x, exp(-9.28 * nDotV * log(2.0))) * r.x + r.y;
    return vec2(-1.04, 1.04) * a + r.zw;
}

// What to multiply specular by to give back the light a single bounce off the microfacets
// loses. GGX counts only light that leaves after one bounce, so rough surfaces, whose facets
// shadow each other most, come out too dark, and rough metals most, since they reflect
// nearly all of what bounces again. Ess, the share a white surface reflects in one bounce,
// is the scale plus the bias of environmentBrdf. Fdez-Aguera, "A Multiple-Scattering
// Microfacet Model for Real-Time Image-Based Lighting" (2019).
vec3 multiscatterCompensation(vec3 f0, float nDotV, float roughness) {
    vec2 brdf = environmentBrdf(roughness, nDotV);
    float ess = max(brdf.x + brdf.y, 1e-3);
    return 1.0 + f0 * (1.0 - ess) / ess;
}
