// Ways of spreading samples over an area, shared by shadows and ambient occlusion, and
// packing a unit vector into two numbers.
#ifndef SAMPLING_GLSL
#define SAMPLING_GLSL

const float GOLDEN_ANGLE = 2.39996323;

// Scatters samples evenly over a disc. The per-pixel rotation turns banding into fine grain.
vec2 diskSample(int index, int count, float rotation) {
    float radius = sqrt((float(index) + 0.5) / float(count));
    float angle = float(index) * GOLDEN_ANGLE + rotation;
    return radius * vec2(cos(angle), sin(angle));
}

float interleavedGradientNoise(vec2 pixel) {
    return fract(52.9829189 * fract(dot(pixel, vec2(0.06711056, 0.00583715))));
}

// Changes every frame, so temporal anti-aliasing averages the grain away.
float frameNoise(vec2 pixel, int frameNumber) {
    return interleavedGradientNoise(pixel + 5.588238 * float(frameNumber % 64));
}

// Maps a unit vector onto the [-1, 1] square so a normal fits in two channels.
vec2 octahedralEncode(vec3 n) {
    n /= abs(n.x) + abs(n.y) + abs(n.z);
    if (n.z < 0.0) {
        vec2 signs = vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
        n.xy = (1.0 - abs(n.yx)) * signs;
    }
    return n.xy;
}

vec3 octahedralDecode(vec2 e) {
    vec3 n = vec3(e, 1.0 - abs(e.x) - abs(e.y));
    if (n.z < 0.0) {
        vec2 signs = vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
        n.xy = (1.0 - abs(n.yx)) * signs;
    }
    return normalize(n);
}

#endif
