#version 460

// One rectangle, drawn over the finished picture. Sizes are in pixels from the top left
// of the screen. The fragment shader rounds it, borders it and clips it.

layout(location = 0) in vec2 inPosition;
layout(location = 1) in vec2 inSize;
layout(location = 2) in vec2 inUvMin;
layout(location = 3) in vec2 inUvMax;
layout(location = 4) in vec4 inColor;
layout(location = 5) in vec4 inBorderColor;
layout(location = 6) in vec4 inClip;
layout(location = 7) in vec3 inShape;
layout(location = 8) in int inTexture;

layout(push_constant) uniform Screen
{
    vec2 size;
} screen;

layout(location = 0) out vec2 uv;
layout(location = 1) out vec4 color;
layout(location = 2) out vec4 borderColor;
layout(location = 3) out vec2 pixel;
layout(location = 4) flat out vec4 quad;
layout(location = 5) flat out vec4 clip;
layout(location = 6) flat out vec3 shape;
layout(location = 7) flat out int textureIndex;

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
    borderColor = inBorderColor;
    pixel = position;
    quad = vec4(inPosition, inSize);
    clip = inClip;
    shape = inShape;
    textureIndex = inTexture;
}
