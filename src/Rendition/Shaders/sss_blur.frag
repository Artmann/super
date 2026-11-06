#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "sampling.glsl"
#include "material_data.glsl"

// Spreads the diffuse light of subsurface materials along the surface, once across and
// once down the screen. Jimenez et al., "Separable Subsurface Scattering" (2015), showed
// that two one-way blurs are close to the real thing. Each tap is weighed by how far
// apart the two points are in the world, so the light stays on its own surface and
// doesn't bleed across a gap to whatever is behind.

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D scatterTexture;
layout(set = 1, binding = 1) uniform sampler2D depthTexture;

layout(std430, set = 2, binding = 1) readonly buffer Materials {
    Material materials[];
};

layout(push_constant) uniform Blur {
    vec4 direction; // xy: one pixel across the blur; z: 1 for the last pass.
} blur;

layout(location = 0) out vec4 outColor;

// Taps on each side of the centre.
const int TAPS = 8;

// Far-off faces are tiny on screen, but a close-up stays within this, so a blur never
// costs more than a fixed number of taps however near the camera gets.
const float MAX_REACH_PIXELS = 96.0;

// The spread is a radius that most of the light stays within, about two standard deviations.
const float RADIUS_TO_SIGMA = 0.5;

vec3 worldAt(vec2 uv, float depth) {
    vec4 world = frame.inverseViewProjection * vec4(uv * 2.0 - 1.0, depth, 1.0);
    return world.xyz / world.w;
}

void main() {
    vec4 centre = texture(scatterTexture, inUv);

    // The last pass adds over the lit image, so alpha 0 leaves it as it is.
    float keep = blur.direction.z > 0.5 ? 0.0 : centre.a;
    if (centre.a < 0.5) {
        outColor = vec4(0.0);
        return;
    }

    vec4 spread = materials[int(centre.a - 0.5)].subsurface;
    vec3 sigma = max(spread.rgb * RADIUS_TO_SIGMA, vec3(1e-6));

    vec2 size = vec2(textureSize(scatterTexture, 0));
    float depth = texture(depthTexture, inUv).r;
    vec3 position = worldAt(inUv, depth);
    vec2 step = blur.direction.xy / size;
    float metresPerPixel = length(worldAt(inUv + step, depth) - position);

    // Reach three standard deviations of the widest colour, where almost nothing is left.
    float reachPixels = min(3.0 * spread.w * RADIUS_TO_SIGMA / max(metresPerPixel, 1e-9), MAX_REACH_PIXELS);
    if (reachPixels < 0.5) {
        outColor = vec4(centre.rgb, keep);
        return;
    }

    // A small shift that changes every frame turns the gaps between taps into grain,
    // which anti-aliasing averages away.
    float jitter = frameNoise(gl_FragCoord.xy, frame.frameNumber) - 0.5;

    vec3 sum = centre.rgb;
    vec3 weights = vec3(1.0);
    for (int i = -TAPS; i <= TAPS; i++) {
        if (i == 0) {
            continue;
        }

        vec2 uv = inUv + step * (reachPixels * (float(i) + jitter) / float(TAPS));
        if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) {
            continue;
        }

        vec4 tap = texture(scatterTexture, uv);
        if (tap.a < 0.5) {
            continue;
        }

        float distance = length(worldAt(uv, texture(depthTexture, uv).r) - position);
        vec3 weight = exp(-(distance * distance) / (2.0 * sigma * sigma));
        sum += tap.rgb * weight;
        weights += weight;
    }

    outColor = vec4(sum / weights, keep);
}
