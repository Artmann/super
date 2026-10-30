#version 460

layout(location = 0) in vec2 inUv;

layout(set = 0, binding = 0) uniform sampler2D sourceTexture;

layout(location = 0) out vec4 outColor;

const float REDUCE_MIN = 1.0 / 128.0;
const float REDUCE_SCALE = 1.0 / 8.0;
const float MAX_SPAN = 8.0;

// The eye judges edges by perceived brightness, which the square root approximates.
float luma(vec3 linear) {
    return sqrt(dot(linear, vec3(0.299, 0.587, 0.114)));
}

// FXAA (Lottes): finds which way an edge runs from the brightness of neighbouring
// pixels, then blurs along it and leaves everything else alone.
void main() {
    vec2 texel = 1.0 / vec2(textureSize(sourceTexture, 0));
    vec3 center = texture(sourceTexture, inUv).rgb;
    float lumaM = luma(center);
    float lumaNW = luma(texture(sourceTexture, inUv + vec2(-1.0, -1.0) * texel).rgb);
    float lumaNE = luma(texture(sourceTexture, inUv + vec2(1.0, -1.0) * texel).rgb);
    float lumaSW = luma(texture(sourceTexture, inUv + vec2(-1.0, 1.0) * texel).rgb);
    float lumaSE = luma(texture(sourceTexture, inUv + vec2(1.0, 1.0) * texel).rgb);
    float lumaMin = min(lumaM, min(min(lumaNW, lumaNE), min(lumaSW, lumaSE)));
    float lumaMax = max(lumaM, max(max(lumaNW, lumaNE), max(lumaSW, lumaSE)));

    vec2 direction = vec2(-((lumaNW + lumaNE) - (lumaSW + lumaSE)), (lumaNW + lumaSW) - (lumaNE + lumaSE));
    float reduce = max((lumaNW + lumaNE + lumaSW + lumaSE) * 0.25 * REDUCE_SCALE, REDUCE_MIN);
    float scale = 1.0 / (min(abs(direction.x), abs(direction.y)) + reduce);
    direction = clamp(direction * scale, vec2(-MAX_SPAN), vec2(MAX_SPAN)) * texel;

    vec3 near = 0.5 * (texture(sourceTexture, inUv + direction * (1.0 / 3.0 - 0.5)).rgb
        + texture(sourceTexture, inUv + direction * (2.0 / 3.0 - 0.5)).rgb);
    vec3 far = near * 0.5 + 0.25 * (texture(sourceTexture, inUv - direction * 0.5).rgb
        + texture(sourceTexture, inUv + direction * 0.5).rgb);

    // Reaching too far picks up another surface, so fall back to the shorter blur.
    float lumaFar = luma(far);
    outColor = vec4(lumaFar < lumaMin || lumaFar > lumaMax ? near : far, 1.0);
}
