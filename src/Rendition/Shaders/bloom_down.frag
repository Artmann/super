#version 460

layout(location = 0) in vec2 inUv;

layout(set = 0, binding = 0) uniform sampler2D sourceTexture;

layout(location = 0) out vec4 outColor;

// A single tiny highlight must not become a flickering blob.
const vec3 BRIGHTEST = vec3(20.0);

void main() {
    vec2 texel = 1.0 / vec2(textureSize(sourceTexture, 0));
    vec3 color = texture(sourceTexture, inUv + texel * vec2(-1.0, -1.0)).rgb
        + texture(sourceTexture, inUv + texel * vec2(1.0, -1.0)).rgb
        + texture(sourceTexture, inUv + texel * vec2(-1.0, 1.0)).rgb
        + texture(sourceTexture, inUv + texel * vec2(1.0, 1.0)).rgb;
    outColor = vec4(min(color * 0.25, BRIGHTEST), 1.0);
}
