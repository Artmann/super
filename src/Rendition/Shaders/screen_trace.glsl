// What the passes that walk rays through the depth buffer share: contact shadows in lighting.frag, the traced
// reflections in reflections.frag, the ambient occlusion in ao.frag and ao_blur.frag, and the lighting's reading of
// them. Needs common.glsl first.
#ifndef SCREEN_TRACE_GLSL
#define SCREEN_TRACE_GLSL

// The clear lacquer over car paint is very smooth and reflects like glass.
const float CLEARCOAT_ROUGHNESS = 0.06;
const vec3 CLEARCOAT_F0 = vec3(0.04);

// Surfaces rougher than this aren't traced: their reflection is too blurred to tell from the probes'.
const float TRACE_MAX_ROUGHNESS = 0.45;

// How far a point is behind what the camera sees there. Under perspective, clip z is the
// same everywhere and reversed-Z stores clip z / w, so dividing recovers the distance of
// the stored surface. Under orthographic, w is always 1 and depth is even, so the
// difference only needs scaling by how much depth one world unit is worth.
float depthGap(sampler2D depthTexture, vec4 clip, out vec2 uv) {
    uv = clip.xy / clip.w * 0.5 + 0.5;
    float stored = texture(depthTexture, uv).r;
    if (stored <= 0.0) {
        return -1.0;
    }

    mat4 m = frame.viewProjection;
    bool parallel = m[0][3] == 0.0 && m[1][3] == 0.0 && m[2][3] == 0.0;
    if (parallel) {
        return (stored - clip.z) / length(vec3(m[0][2], m[1][2], m[2][2]));
    }

    return clip.w - clip.z / stored;
}

// Where the camera's ray through this pixel crosses the puddles' water level. The depth
// buffer holds the ground under the water, and a reflection traced from there would run
// straight into the sides of its own hollow. Two points along the pixel's ray, at its depth
// and nearer, give the ray under any projection.
vec3 onWaterSurface(vec4 clip, vec3 ground) {
    vec4 nearer = frame.inverseViewProjection * vec4(clip.xy, min(clip.z * 2.0 + 1e-4, 1.0), 1.0);
    vec3 towardsCamera = nearer.xyz / nearer.w - ground;
    float rise = frame.puddles.x - ground.y;
    if (rise <= 0.0 || towardsCamera.y <= 1e-6) {
        return ground;
    }

    return ground + towardsCamera * (rise / towardsCamera.y);
}

// How much of a surface's reflection is traced on screen instead of read from the probes: all of it for a smooth
// surface, fading to none at TRACE_MAX_ROUGHNESS. Coated surfaces are traced for their mirror-like coat, which then
// serves both layers. reflections.frag traces where this is above 0, and the lighting weighs what it traced by it.
float traceStrength(float roughness, float clearcoat) {
    if (isOff(OFF_REFLECTIONS) || isOff(OFF_SPECULAR) || frame.historyValid == 0) {
        return 0.0;
    }

    return clearcoat > 0.0 ? 1.0 : 1.0 - smoothstep(TRACE_MAX_ROUGHNESS * 0.5, TRACE_MAX_ROUGHNESS, roughness);
}

// At half resolution one pixel of each block of four is traced, a different one each frame, so over four frames
// every pixel has had its own ray and anti-aliasing blends them into a full picture. Reflections and ambient
// occlusion both work this way.
ivec2 tracedCorner() {
    int turn = frame.frameNumber & 3;
    return ivec2(turn & 1, (turn >> 1) ^ (turn & 1));
}

// The screen pixel that a cell of a half-resolution target traces this frame.
ivec2 tracedPixel(ivec2 cell, ivec2 screenSize) {
    return min(cell * 2 + tracedCorner(), screenSize - 1);
}

#endif
