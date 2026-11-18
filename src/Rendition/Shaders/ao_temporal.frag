#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "sampling.glsl"
#include "screen_trace.glsl"

layout(set = 1, binding = 0) uniform sampler2D occlusionTexture;  // This frame's, as ao.frag traced it.
layout(set = 1, binding = 1) uniform sampler2D bounceTexture;
layout(set = 1, binding = 2) uniform sampler2D occlusionHistory;  // What this pass wrote last frame.
layout(set = 1, binding = 3) uniform sampler2D bounceHistory;
layout(set = 1, binding = 4) uniform sampler2D depthTexture;
layout(set = 1, binding = 5) uniform sampler2D motionTexture;     // see Passes/Motion.cs

layout(push_constant) uniform Temporal {
    int scale;        // Screen pixels per pixel of the occlusion, along each axis: 1 or 2.
    int historyValid; // 1 when the history holds last frame's occlusion.
} temporal;

// xy: the bent normal; z: visibility; a: how many frames it is the average of, as a share of MAX_FRAMES.
layout(location = 0) out vec4 outOcclusion;
// rgb: the bounced light; a: the depth of the pixel it was traced for, which next frame checks its history by.
layout(location = 1) out vec4 outBounce;

// How many frames are averaged. More is smoother, and slower to follow a light or a figure that moves.
const float MAX_FRAMES = 8.0;

// How far history may lie in front of or behind the surface and still be the same surface: this share of the
// occlusion's radius, and this many times what the surface's distance changes by from one pixel to the next.
const float DISTANCE_TOLERANCE = 0.05;
const float SLOPE_TOLERANCE = 2.0;

// The most a pixel's slope may count for, as a share of its distance, so something a pixel thin doesn't accept the
// wall far behind it.
const float SLOPE_LIMIT = 0.02;

// How far history's visibility may lie outside what the pixels around trace this frame before it is pulled back in.
// The rays of one frame are too few to say more than that, so history is never given up for straying.
const float VISIBILITY_MARGIN = 0.1;

// See taa.frag.
const float MOTION_TOLERANCE = 0.01;
const float MOTION_PRECISION = 1.0 / 512.0;

// How far along the view the surface in a pixel of the screen is, under either projection. The sky is as far as the
// depth can say.
float distanceAlong(ivec2 pixel, ivec2 size) {
    ivec2 inside = clamp(pixel, ivec2(0), size - 1);
    float depth = max(texelFetch(depthTexture, inside, 0).r, 1e-7);
    vec2 ndc = (vec2(inside) + 0.5) / vec2(size) * 2.0 - 1.0;
    vec4 world = frame.inverseViewProjection * vec4(ndc, depth, 1.0);
    return gapBehind(frame.viewProjection, frame.viewProjection * (world / world.w), 1.0);
}

// Averages the occlusion and the bounce over the last frames. Each frame traces a few rays a pixel, in new directions
// every time, so the average of eight frames is as smooth as eight times the rays, and ao_blur.frag is left little
// grain to blur away. History is followed to where the surface was last frame, and dropped where what was there is
// not this surface.
void main() {
    ivec2 own = ivec2(gl_FragCoord.xy);
    ivec2 size = textureSize(depthTexture, 0);
    ivec2 pixel = temporal.scale > 1 ? tracedPixel(own, size) : own;
    float depth = texelFetch(depthTexture, pixel, 0).r;
    vec4 traced = texelFetch(occlusionTexture, own, 0);
    vec3 bounce = texelFetch(bounceTexture, own, 0).rgb;

    if (depth == 0.0) {
        outOcclusion = vec4(0.5, 0.5, 1.0, 0.0);
        outBounce = vec4(0.0);
        return;
    }

    // The pixel's distance along the view, and how much it changes to the next pixel of the occlusion. Each way takes
    // the smaller change of its two sides, so a pixel at the edge of an object measures the object and not the drop
    // to the wall behind it.
    vec2 uv = (vec2(pixel) + 0.5) / vec2(size);
    vec2 ndc = uv * 2.0 - 1.0;
    vec4 world = frame.inverseViewProjection * vec4(ndc, depth, 1.0);
    vec4 now = frame.viewProjection * (world / world.w);
    float along = distanceAlong(pixel, size);
    ivec2 right = ivec2(temporal.scale, 0);
    ivec2 down = ivec2(0, temporal.scale);
    float slope = min(abs(distanceAlong(pixel + right, size) - along), abs(distanceAlong(pixel - right, size) - along))
        + min(abs(distanceAlong(pixel + down, size) - along), abs(distanceAlong(pixel - down, size) - along));
    float steepest = SLOPE_LIMIT * abs(along);
    if (!(slope < steepest)) { // Written so a slope that isn't a number is limited too.
        slope = steepest;
    }

    // History has no anti-aliasing shift of its own, so this frame's comes out of the lookup, as in taa.frag: a still
    // camera then reads each pixel's own history exactly.
    vec4 locked = frame.inverseViewProjection * vec4(ndc + frame.finish.zw, depth, 1.0);
    vec4 previous = frame.previousViewProjection * locked;
    vec2 shift = previous.xy / previous.w * 0.5 + 0.5 - uv;
    bool lost = temporal.historyValid == 0 || previous.w <= 0.0;
    mat4 camera = frame.previousViewProjection;

    // A surface that moved by itself follows its motion vector, and is taken to be as far away as it is now.
    vec2 motion = texelFetch(motionTexture, pixel, 0).xy;
    vec4 surface = frame.previousViewProjection * world;
    vec2 cameraMotion = (surface.xy / surface.w - (ndc - frame.finish.zw)) * 0.5 * vec2(size);
    if (surface.w > 0.0 && length(motion - cameraMotion) > MOTION_TOLERANCE + MOTION_PRECISION * length(cameraMotion)) {
        shift = motion / vec2(size);
        previous = now;
        camera = frame.viewProjection;
        lost = temporal.historyValid == 0;
    }

    // The four pixels of history around where the surface was, each counted only if it was this surface.
    ivec2 historySize = textureSize(occlusionHistory, 0);
    vec2 spot = (vec2(own) + 0.5) + shift * vec2(historySize) - 0.5;
    ivec2 corner = ivec2(floor(spot));
    vec2 part = spot - vec2(corner);
    float tolerance = DISTANCE_TOLERANCE * frame.occlusion.x + SLOPE_TOLERANCE * slope;
    vec3 direction = vec3(0.0);
    vec3 visibilityAndFrames = vec3(0.0);
    vec3 bounced = vec3(0.0);
    float weights = 0.0;
    // Written so a spot that isn't a number is lost too.
    if (!lost && all(greaterThan(spot, vec2(-1.0))) && all(lessThan(spot, vec2(historySize)))) {
        for (int i = 0; i < 4; i++) {
            ivec2 offset = ivec2(i & 1, i >> 1);
            ivec2 cell = corner + offset;
            if (any(lessThan(cell, ivec2(0))) || any(greaterThanEqual(cell, historySize))) {
                continue;
            }

            vec4 old = texelFetch(bounceHistory, cell, 0);
            if (old.a <= 0.0 || abs(gapBehind(camera, previous, old.a)) > tolerance) {
                continue;
            }

            vec2 each = mix(1.0 - part, part, vec2(offset));
            float weight = each.x * each.y;
            vec4 occlusion = texelFetch(occlusionHistory, cell, 0);
            direction += octahedralDecode(occlusion.xy * 2.0 - 1.0) * weight;
            visibilityAndFrames += vec3(occlusion.z, occlusion.a, 0.0) * weight;
            bounced += old.rgb * weight;
            weights += weight;
        }
    }

    vec3 bent = octahedralDecode(traced.xy * 2.0 - 1.0);
    float visibility = traced.z;
    float frames = 1.0;
    if (weights > 1e-3) {
        // What moved since last frame changes the occlusion of surfaces that held still, such as the floor a figure
        // walks across. History far from anything the pixels around trace now is pulled back.
        float lowest = 1.0;
        float highest = 0.0;
        ivec2 last = textureSize(occlusionTexture, 0) - 1;
        for (int y = -1; y <= 1; y++) {
            for (int x = -1; x <= 1; x++) {
                float around = texelFetch(occlusionTexture, clamp(own + ivec2(x, y), ivec2(0), last), 0).z;
                lowest = min(lowest, around);
                highest = max(highest, around);
            }
        }

        float remembered = visibilityAndFrames.x / weights;
        float kept = clamp(remembered, lowest - VISIBILITY_MARGIN, highest + VISIBILITY_MARGIN);

        frames = min(visibilityAndFrames.y / weights * MAX_FRAMES + 1.0, MAX_FRAMES);
        float share = 1.0 / frames;
        visibility = mix(kept, visibility, share);
        bounce = mix(bounced / weights, bounce, share);
        vec3 blended = mix(direction / weights, bent, share);
        if (dot(blended, blended) > 1e-12) {
            bent = normalize(blended);
        }
    }

    outOcclusion = vec4(octahedralEncode(bent) * 0.5 + 0.5, visibility, frames / MAX_FRAMES);
    outBounce = vec4(bounce, depth);
}
