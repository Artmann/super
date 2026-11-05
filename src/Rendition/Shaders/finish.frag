#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "sampling.glsl"

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D sourceTexture;
layout(set = 1, binding = 1) uniform sampler2D heldTexture;

// Must match TransitionConstants in Passes/TransitionConstants.cs.
layout(push_constant) uniform Transition {
    vec4 colorAmount; // rgb: the fade's colour; a: how far along.
    vec4 shape;       // xy: which way a wipe travels; zw: where an iris opens from.
    vec4 style;       // x: none, fade, dissolve, wipe or iris; y: the edge's softness.
} transition;

layout(location = 0) out vec4 outColor;

const float DITHER_STRENGTH = 1.0 / 255.0;

const int STYLE_FADE = 1;
const int STYLE_DISSOLVE = 2;
const int STYLE_WIPE = 3;
const int STYLE_IRIS = 4;

// Restores crispness lost to anti-aliasing. Staying within the neighbours' range avoids
// bright halos along edges.
vec3 sharpened(sampler2D image, vec2 uv, vec2 texel) {
    vec3 center = texture(image, uv).rgb;
    vec3 north = texture(image, uv + vec2(0.0, -texel.y)).rgb;
    vec3 south = texture(image, uv + vec2(0.0, texel.y)).rgb;
    vec3 east = texture(image, uv + vec2(texel.x, 0.0)).rgb;
    vec3 west = texture(image, uv + vec2(-texel.x, 0.0)).rgb;

    vec3 low = min(center, min(min(north, south), min(east, west)));
    vec3 high = max(center, max(max(north, south), max(east, west)));
    return clamp(center + (center - (north + south + east + west) * 0.25) * frame.finish.x, low, high);
}

// How much of the new frame shows here, from 0 to 1. `along` runs from 0 where the edge
// sets off to 1 where it finishes; the edge moves past both ends so each end is all one picture.
float revealed(float along, float amount, float softness) {
    float edge = amount * (1.0 + softness);
    return 1.0 - smoothstep(edge - softness, edge, along);
}

// How much of the new frame shows here, for the styles that show a held picture.
float shown(vec2 aspect) {
    int kind = int(transition.style.x + 0.5);
    float amount = transition.colorAmount.a;
    float softness = max(transition.style.y, 1e-4);
    if (kind == STYLE_DISSOLVE) {
        return amount;
    }

    if (kind == STYLE_WIPE) {
        // Measured across the screen as it is, so a diagonal wipe crosses corner to corner.
        vec2 direction = transition.shape.xy;
        vec2 p = (inUv - 0.5) * aspect;
        float reach = 0.5 * dot(abs(direction), aspect);
        return revealed((dot(p, direction) + reach) / (2.0 * reach), amount, softness);
    }

    // An iris: a circle on screen, growing until it covers the furthest corner.
    vec2 center = transition.shape.zw;
    vec2 corner = max(center, 1.0 - center) * aspect;
    float distanceOut = length((inUv - center) * aspect);
    return revealed(distanceOut / length(corner), amount, softness);
}

// The last touches: restores crispness, moves between pictures, darkens the corners and adds
// fine noise. An 8-bit screen can't show a slow gradient smoothly, and the noise hides those steps.
void main() {
    vec2 size = vec2(textureSize(sourceTexture, 0));
    vec2 texel = 1.0 / size;
    vec3 color = sharpened(sourceTexture, inUv, texel);

    int kind = int(transition.style.x + 0.5);
    if (kind == STYLE_FADE) {
        color = mix(color, transition.colorAmount.rgb, transition.colorAmount.a);
    } else if (kind >= STYLE_DISSOLVE) {
        vec2 aspect = vec2(size.x / size.y, 1.0);
        color = mix(sharpened(heldTexture, inUv, texel), color, shown(aspect));
    }

    vec2 fromCenter = inUv - 0.5;
    color *= 1.0 - frame.grade.w * smoothstep(0.1, 0.5, dot(fromCenter, fromCenter));

    // Noise belongs in the same steps as the screen, which aren't linear in brightness.
    float noise = frameNoise(gl_FragCoord.xy, frame.frameNumber) - 0.5;
    vec3 display = max(sqrt(color) + noise * (DITHER_STRENGTH + frame.finish.y), vec3(0.0));
    outColor = vec4(display * display, 1.0);
}
