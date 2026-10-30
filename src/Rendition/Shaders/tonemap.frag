#version 460

layout(location = 0) in vec2 inUv;

layout(set = 0, binding = 0) uniform sampler2D hdrTexture;
layout(set = 0, binding = 1) uniform sampler2D bloomTexture;

layout(location = 0) out vec4 outColor;

const float BLOOM_STRENGTH = 0.05;

// Krzysztof Narkowicz's fit of the ACES filmic curve.
vec3 acesFilmic(vec3 x) {
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

void main() {
    // Every pixel gives a little light to its surroundings, so bright things glow by
    // themselves and no brightness threshold is needed.
    vec3 color = mix(texture(hdrTexture, inUv).rgb, texture(bloomTexture, inUv).rgb, BLOOM_STRENGTH);

    // The target is an sRGB format, so the hardware applies gamma on write.
    outColor = vec4(acesFilmic(color), 1.0);
}
