#version 460

// Fills a target with one colour given as push constants. The GPU layer's tests draw with it.
layout(push_constant) uniform Constants {
    vec4 color;
} constants;

layout(location = 0) out vec4 outColor;

void main() {
    outColor = constants.color;
}
