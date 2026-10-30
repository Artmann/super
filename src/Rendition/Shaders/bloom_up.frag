#version 460

layout(location = 0) in vec2 inUv;

layout(set = 0, binding = 0) uniform sampler2D sharpTexture;
layout(set = 0, binding = 1) uniform sampler2D wideTexture;

layout(location = 0) out vec4 outColor;

const float WIDE_SHARE = 0.6;

// Adds the wider blur from the level below. The tent filter hides its blocky pixels.
void main() {
    vec2 t = 1.0 / vec2(textureSize(wideTexture, 0));
    vec3 wide = texture(wideTexture, inUv).rgb * 4.0
        + (texture(wideTexture, inUv + vec2(t.x, 0.0)).rgb + texture(wideTexture, inUv - vec2(t.x, 0.0)).rgb
            + texture(wideTexture, inUv + vec2(0.0, t.y)).rgb + texture(wideTexture, inUv - vec2(0.0, t.y)).rgb) * 2.0
        + texture(wideTexture, inUv + t).rgb + texture(wideTexture, inUv - t).rgb
        + texture(wideTexture, inUv + vec2(t.x, -t.y)).rgb + texture(wideTexture, inUv + vec2(-t.x, t.y)).rgb;
    outColor = vec4(mix(texture(sharpTexture, inUv).rgb, wide / 16.0, WIDE_SHARE), 1.0);
}
