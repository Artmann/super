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

// A Catmull-Rom lookup: sharper than blending the four nearest texels, as it reads the
// sixteen round them. Five linear reads stand in for those, leaving out the four corners,
// which weigh least. The image's sampler must blend linearly.
vec3 catmullRom(sampler2D image, vec2 uv) {
    vec2 size = vec2(textureSize(image, 0));
    vec2 position = uv * size - 0.5;
    vec2 base = floor(position);
    vec2 f = position - base;

    vec2 w0 = f * (-0.5 + f * (1.0 - 0.5 * f));
    vec2 w1 = 1.0 + f * f * (-2.5 + 1.5 * f);
    vec2 w2 = f * (0.5 + f * (2.0 - 1.5 * f));
    vec2 w3 = f * f * (-0.5 + 0.5 * f);
    vec2 w12 = w1 + w2;

    vec2 uv0 = (base - 0.5) / size;
    vec2 uv3 = (base + 2.5) / size;
    vec2 uv12 = (base + 0.5 + w2 / w12) / size;

    vec3 color = texture(image, vec2(uv12.x, uv0.y)).rgb * w12.x * w0.y
        + texture(image, vec2(uv0.x, uv12.y)).rgb * w0.x * w12.y
        + texture(image, uv12).rgb * w12.x * w12.y
        + texture(image, vec2(uv3.x, uv12.y)).rgb * w3.x * w12.y
        + texture(image, vec2(uv12.x, uv3.y)).rgb * w12.x * w3.y;
    float total = w12.x * w0.y + w0.x * w12.y + w12.x * w12.y + w3.x * w12.y + w12.x * w3.y;
    return max(color / total, vec3(0.0));
}

#endif
