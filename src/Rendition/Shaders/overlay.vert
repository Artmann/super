#version 460

// One flat rectangle, drawn over the finished picture. Sizes are in pixels from the
// top left of the screen.

layout(location = 0) in vec2 inPosition;
layout(location = 1) in vec2 inSize;
layout(location = 2) in vec2 inUvMin;
layout(location = 3) in vec2 inUvMax;
layout(location = 4) in vec4 inColor;

layout(push_constant) uniform Screen
{
    vec2 size;
} screen;

layout(location = 0) out vec2 uv;
layout(location = 1) out vec4 color;

const vec2 Corners[6] = vec2[](
    vec2(0.0, 0.0), vec2(1.0, 0.0), vec2(0.0, 1.0),
    vec2(0.0, 1.0), vec2(1.0, 0.0), vec2(1.0, 1.0));

void main()
{
    vec2 corner = Corners[gl_VertexIndex];
    vec2 position = inPosition + inSize * corner;
    gl_Position = vec4(position / screen.size * 2.0 - 1.0, 0.0, 1.0);
    uv = mix(inUvMin, inUvMax, corner);
    color = inColor;
}
