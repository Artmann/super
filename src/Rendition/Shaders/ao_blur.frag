#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "sampling.glsl"
#include "screen_trace.glsl"

layout(set = 1, binding = 0) uniform sampler2D occlusionTexture; // a: how settled it is, from 0 to 1.
layout(set = 1, binding = 1) uniform sampler2D bounceTexture;
layout(set = 1, binding = 2) uniform sampler2D depthTexture;
layout(set = 1, binding = 3) uniform sampler2D normalRoughnessAoTexture;

layout(push_constant) uniform Blur {
    ivec2 step; // One pixel of the occlusion along the way this pass blurs.
    int scale;  // Screen pixels per pixel of the occlusion, along each axis: 1 or 2.
} blur;

layout(location = 0) out vec4 outOcclusion;
layout(location = 1) out vec4 outBounce;

// How far each way the blur reaches while the occlusion is one frame's grain, at full and at half resolution.
const int RADIUS = 3;
const int HALF_RADIUS = 1;

// How settled the occlusion has to be for the blur to reach one pixel less, and then one less again.
const float SETTLING = 0.25;
const float SETTLED = 0.75;

// At half resolution the blur always reaches one pixel, which is two on screen, so its neighbours weigh this much
// once the occlusion has settled.
const float HALF_SETTLED_WEIGHT = 0.5;

// How far a neighbour may lie off the pixel's own surface and still share its occlusion: this share of the
// occlusion's radius, plus this much for each metre between the two, which forgives a normal map's tilt and the
// rounding of a curved surface.
const float PLANE_TOLERANCE = 0.05;
const float PLANE_SLOPE = 0.25;

// How fast a neighbour stops counting as its normal turns away from the pixel's: at 45 degrees it counts for 6%.
const float NORMAL_POWER = 8.0;

// The screen pixel a pixel of the occlusion was traced for this frame.
ivec2 screenPixel(ivec2 cell, ivec2 size) {
    return blur.scale > 1 ? tracedPixel(cell, size) : cell;
}

vec3 worldAt(ivec2 pixel, float depth, ivec2 size) {
    vec2 uv = (vec2(pixel) + 0.5) / vec2(size);
    vec4 world = frame.inverseViewProjection * vec4(uv * 2.0 - 1.0, depth, 1.0);
    return world.xyz / world.w;
}

// How far the blur reaches each way: less as the occlusion settles, since ao_temporal.frag has then averaged most of
// the grain away already and a wide blur would only soften the creases.
int reach(float settled) {
    if (blur.scale > 1) {
        return HALF_RADIUS;
    }

    return settled >= SETTLED ? RADIUS - 2 : settled >= SETTLING ? RADIUS - 1 : RADIUS;
}

// One direction of a blur that stays on the pixel's own surface. A neighbour counts by how near it lies to the plane
// through the pixel, along the pixel's normal, and by how alike their normals are, so the occlusion of a stair's
// tread doesn't spread onto its riser or onto the tread below, however small the stair is on screen. Comparing depths
// alone can't tell those apart from far away.
void main() {
    ivec2 own = ivec2(gl_FragCoord.xy);
    ivec2 size = textureSize(depthTexture, 0);
    ivec2 ownPixel = screenPixel(own, size);
    float depth = texelFetch(depthTexture, ownPixel, 0).r;
    if (depth == 0.0) {
        outOcclusion = vec4(0.5, 0.5, 1.0, 0.0);
        outBounce = vec4(0.0);
        return;
    }

    vec4 ownTap = texelFetch(occlusionTexture, own, 0);
    float settled = ownTap.a;
    vec3 position = worldAt(ownPixel, depth, size);
    vec3 normal = octahedralDecode(texelFetch(normalRoughnessAoTexture, ownPixel, 0).xy);
    float tolerance = PLANE_TOLERANCE * frame.occlusion.x;
    float besideWeight = blur.scale > 1 ? mix(1.0, HALF_SETTLED_WEIGHT, settled) : 1.0;

    ivec2 last = textureSize(occlusionTexture, 0) - 1;
    vec3 direction = vec3(0.0);
    float visibility = 0.0;
    vec3 bounce = vec3(0.0);
    float weights = 0.0;
    int radius = reach(settled);
    for (int i = -radius; i <= radius; i++) {
        ivec2 cell = clamp(own + i * blur.step, ivec2(0), last);
        ivec2 pixel = screenPixel(cell, size);
        float tapDepth = texelFetch(depthTexture, pixel, 0).r;
        if (tapDepth == 0.0) {
            continue;
        }

        vec3 offset = worldAt(pixel, tapDepth, size) - position;
        float off = dot(offset, normal) / (tolerance + PLANE_SLOPE * length(offset));
        vec3 tapNormal = octahedralDecode(texelFetch(normalRoughnessAoTexture, pixel, 0).xy);
        float weight = exp(-off * off) * pow(max(dot(normal, tapNormal), 0.0), NORMAL_POWER);
        if (i != 0) {
            weight *= besideWeight;
        }

        vec4 tap = texelFetch(occlusionTexture, cell, 0);
        direction += octahedralDecode(tap.xy * 2.0 - 1.0) * weight;
        visibility += tap.z * weight;
        bounce += texelFetch(bounceTexture, cell, 0).rgb * weight;
        weights += weight;
    }

    // The pixel's own tap always counts in full, so there is something to divide by.
    vec3 bent = dot(direction, direction) > 1e-12 ? normalize(direction) : octahedralDecode(ownTap.xy * 2.0 - 1.0);
    outOcclusion = vec4(octahedralEncode(bent) * 0.5 + 0.5, visibility / weights, settled);
    outBounce = vec4(bounce / weights, 1.0);
}
