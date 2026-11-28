#version 460

// A corner at a position in clip space, moved by its instance's offset and coloured by its instance. The GPU layer's
// tests draw meshes with it.
layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec2 inOffset;
layout(location = 2) in vec4 inColor;

layout(location = 0) out vec4 outColor;

void main() {
    outColor = inColor;
    gl_Position = vec4(inPosition.xy + inOffset, inPosition.z, 1.0);
}
