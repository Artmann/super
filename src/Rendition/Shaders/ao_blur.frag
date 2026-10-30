#version 460

layout(location = 0) in vec2 inUv;

layout(set = 0, binding = 0) uniform sampler2D occlusionTexture;
layout(set = 0, binding = 1) uniform sampler2D depthTexture;

layout(location = 0) out vec4 outOcclusion;

const int RADIUS = 3;

// Averages away the grain, but not across the edges of objects.
void main() {
    vec2 texel = 1.0 / vec2(textureSize(occlusionTexture, 0));
    float center = texture(depthTexture, inUv).r;
    float sum = 0.0;
    float weights = 0.0;
    for (int y = -RADIUS; y <= RADIUS; y++) {
        for (int x = -RADIUS; x <= RADIUS; x++) {
            vec2 uv = inUv + vec2(x, y) * texel;
            float difference = abs(texture(depthTexture, uv).r / max(center, 1e-6) - 1.0);
            float weight = exp(-difference * 20.0);
            sum += texture(occlusionTexture, uv).r * weight;
            weights += weight;
        }
    }
    outOcclusion = vec4(sum / weights);
}
