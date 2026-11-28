#version 460

layout(location = 0) in vec2 inUv;

// The mip above, for halving, or a picture of the same size, for copying.
layout(set = 0, binding = 0) uniform sampler2D sourceTexture;

layout(location = 0) out vec4 outColor;

// Sampled linearly at the centre of a pixel of the smaller mip, this is the average of the 2x2 texels above it, as a
// linear blit gives, and at odd sizes it weighs the texels the same way the blit does.
void main() {
    outColor = texture(sourceTexture, inUv);
}
