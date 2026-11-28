#version 460

// The colour test_mesh.vert gives, or blue when override constant 0 is set. The GPU layer's tests draw with it.
layout(constant_id = 0) const bool BLUE = false;

layout(location = 0) in vec4 inColor;

layout(location = 0) out vec4 outColor;

void main() {
    outColor = BLUE ? vec4(0.0, 0.0, 1.0, 1.0) : inColor;
}
