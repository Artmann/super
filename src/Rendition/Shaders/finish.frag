#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : require
#include "common.glsl"
#include "sampling.glsl"

layout(location = 0) in vec2 inUv;

layout(set = 1, binding = 0) uniform sampler2D sourceTexture;
layout(set = 1, binding = 1) uniform sampler2D heldTexture;

// The renderer's textures, for the screen effects' paper.
layout(set = 2, binding = 0) uniform sampler2D textures[];

// Must match TransitionConstants in Passes/TransitionConstants.cs.
layout(push_constant) uniform Transition {
    vec4 colorAmount; // rgb: the fade's colour; a: how far along.
    vec4 shape;       // xy: which way a wipe travels; zw: where an iris opens from.
    vec4 style;       // x: none, fade, dissolve, wipe or iris; y: the edge's softness; z: 1 when the scene isn't the window's size.
} transition;

layout(location = 0) out vec4 outColor;

const float DITHER_STRENGTH = 1.0 / 255.0;

// The weight each neighbour is taken away with at Sharpen 1, where the contrast is lowest.
// A fifth is as far as it goes before flat areas start to look crunchy.
const float SHARPEN_MOST = 0.2;

const int STYLE_FADE = 1;
const int STYLE_DISSOLVE = 2;
const int STYLE_WIPE = 3;
const int STYLE_IRIS = 4;

// Restores crispness lost to anti-aliasing, with contrast-adaptive sharpening: fine, faint
// detail such as the grain of a texture is sharpened the most, and edges that already
// stand out hardly at all, so they get no bright halos. Staying within the neighbours'
// range keeps it from ringing.
// A scene drawn at another size than the window's is resized here with a Catmull-Rom lookup,
// which keeps it sharper than blending the nearest four pixels, and the neighbours are then
// the scene's pixels round it.
vec3 sharpened(sampler2D image, vec2 uv, vec2 texel) {
    vec3 center = transition.style.z > 0.5 ? catmullRom(image, uv) : texture(image, uv).rgb;
    float strength = frame.finish.x;
    if (strength <= 0.0) {
        return center;
    }

    vec3 north = texture(image, uv + vec2(0.0, -texel.y)).rgb;
    vec3 south = texture(image, uv + vec2(0.0, texel.y)).rgb;
    vec3 east = texture(image, uv + vec2(texel.x, 0.0)).rgb;
    vec3 west = texture(image, uv + vec2(-texel.x, 0.0)).rgb;

    vec3 low = min(center, min(min(north, south), min(east, west)));
    vec3 high = max(center, max(max(north, south), max(east, west)));

    // How much room there is before the darkest neighbour reaches black or the brightest
    // reaches white, compared with the brightest: small where the contrast is already high.
    vec3 room = sqrt(clamp(min(low, 1.0 - high) / max(high, vec3(1e-5)), 0.0, 1.0));
    vec3 weight = -room * strength * SHARPEN_MOST;
    vec3 result = (center + (north + south + east + west) * weight) / (1.0 + 4.0 * weight);
    return clamp(result, low, high);
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

// How much of an edge tint shows here at strength 1: none in the middle, rising to all of it at the edges. The distance
// is a rounded square's, so the tint hugs all four edges rather than only the corners.
float edgeMask(vec2 uv) {
    vec2 out4 = pow(abs(uv - 0.5) * 2.0, vec2(4.0));
    float reach = sqrt(sqrt(out4.x + out4.y));
    return smoothstep(0.35, 1.1, reach) * 1.5;
}

// The screen effects a game drives from play, on the picture's linear colour: paper, then the loss of colour, the edge
// tints, the flash and the fade. Each is skipped at zero.
vec3 screenEffects(vec3 color) {
    int paper = int(frame.screenCanvas.w);
    if (paper >= 0) {
        float tile = frame.screenCanvas.z > 0.0 ? frame.screenCanvas.z : float(textureSize(textures[paper], 0).x);
        vec3 grain = texture(textures[paper], gl_FragCoord.xy / tile).rgb;
        color *= mix(vec3(1.0), grain, frame.screenCanvas.y);
    }

    if (frame.screenCanvas.x > 0.0) {
        color = mix(color, vec3(dot(color, vec3(0.2126, 0.7152, 0.0722))), frame.screenCanvas.x);
    }

    if (frame.edgeTint.a > 0.0 || frame.secondEdgeTint.a > 0.0) {
        float edge = edgeMask(inUv);
        color = mix(color, frame.edgeTint.rgb, clamp(edge * frame.edgeTint.a, 0.0, 1.0));
        color = mix(color, frame.secondEdgeTint.rgb, clamp(edge * frame.secondEdgeTint.a, 0.0, 1.0));
    }

    // A flash lights the picture as a screen print does: it brightens towards the colour and never past white.
    if (frame.screenFlash.a > 0.0) {
        color = 1.0 - (1.0 - clamp(color, 0.0, 1.0)) * (1.0 - frame.screenFlash.rgb * frame.screenFlash.a);
    }

    if (frame.screenFade.a > 0.0) {
        color = mix(color, frame.screenFade.rgb, frame.screenFade.a);
    }

    return color;
}

// The last touches: restores crispness, moves between pictures, darkens the corners and adds
// fine noise. An 8-bit screen can't show a slow gradient smoothly, and the noise hides those steps.
void main() {
    vec2 size = vec2(textureSize(sourceTexture, 0));
    vec2 texel = 1.0 / size;
    // A debug view is shown unsharpened, with no vignette or grain; only the dither below is left.
    bool debugView = isOff(OFF_DEBUG_VIEW);
    vec3 color = debugView ? texture(sourceTexture, inUv).rgb : sharpened(sourceTexture, inUv, texel);

    int kind = int(transition.style.x + 0.5);
    if (kind == STYLE_FADE) {
        color = mix(color, transition.colorAmount.rgb, transition.colorAmount.a);
    } else if (kind >= STYLE_DISSOLVE) {
        // The scene keeps the window's shape whatever its size, so either measures the same.
        vec2 aspect = vec2(size.x / size.y, 1.0);
        color = mix(sharpened(heldTexture, inUv, texel), color, shown(aspect));
    }

    vec2 fromCenter = inUv - 0.5;
    if (!debugView) {
        color *= 1.0 - frame.grade.w * smoothstep(0.1, 0.5, dot(fromCenter, fromCenter));
        color = screenEffects(color);
    }

    // Noise belongs in the same steps as the screen, which aren't linear in brightness.
    float noise = frameNoise(gl_FragCoord.xy, frame.frameNumber) - 0.5;
    float grain = debugView ? 0.0 : frame.finish.y;
    vec3 display = max(sqrt(color) + noise * (DITHER_STRENGTH + grain), vec3(0.0));
    outColor = vec4(display * display, 1.0);
}
