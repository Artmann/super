#version 460

layout(location = 0) in vec2 inUv;

layout(set = 0, binding = 0) uniform sampler2D hdrTexture;

layout(location = 0) out vec4 outColor;

// Krzysztof Narkowicz's fit of the ACES filmic curve.
vec3 acesFilmic(vec3 x) {
    return clamp((x * (2.51 * x + 0.03)) / (x * (2.43 * x + 0.59) + 0.14), 0.0, 1.0);
}

void main() {
    // The swapchain is an sRGB format, so the hardware applies gamma on write.
    outColor = vec4(acesFilmic(texture(hdrTexture, inUv).rgb), 1.0);
}
