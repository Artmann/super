#version 460

// One shape, drawn over the finished picture. Sizes are in pixels from the top left of
// the screen. The fragment shader works out the shape, rounds it, borders it, shades it
// and clips it.

layout(location = 0) in vec2 inPosition;
layout(location = 1) in vec2 inSize;
layout(location = 2) in vec2 inUvMin;
layout(location = 3) in vec2 inUvMax;
layout(location = 4) in vec4 inColor;
layout(location = 5) in vec4 inColor2;
layout(location = 6) in vec4 inBorderColor;
layout(location = 7) in vec4 inClip;
layout(location = 8) in vec4 inRadii;
layout(location = 9) in vec4 inClipRadii;
layout(location = 10) in vec4 inGradientPoints;
layout(location = 11) in vec4 inParams;
layout(location = 12) in vec2 inWidths;
layout(location = 13) in ivec3 inKinds;

layout(push_constant) uniform Screen
{
    vec2 size;
} screen;

layout(location = 0) out vec2 uv;
layout(location = 1) out vec4 color;
layout(location = 2) out vec4 color2;
layout(location = 3) out vec4 borderColor;
layout(location = 4) out vec2 pixel;
layout(location = 5) flat out vec4 quad;
layout(location = 6) flat out vec4 clip;
layout(location = 7) flat out vec4 radii;
layout(location = 8) flat out vec4 clipRadii;
layout(location = 9) flat out vec4 gradientPoints;
layout(location = 10) flat out vec4 params;
layout(location = 11) flat out vec2 widths;
layout(location = 12) flat out ivec3 kinds;

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
    color2 = inColor2;
    borderColor = inBorderColor;
    pixel = position;
    quad = vec4(inPosition, inSize);
    clip = inClip;
    radii = inRadii;
    clipRadii = inClipRadii;
    gradientPoints = inGradientPoints;
    params = inParams;
    widths = inWidths;
    kinds = inKinds;
}
