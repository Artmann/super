#version 460

// The atlas only decides how solid each pixel is. Colors arrive as people write them,
// in sRGB, and the screen wants them linear.

layout(set = 0, binding = 0) uniform sampler2D atlas;

layout(location = 0) in vec2 uv;
layout(location = 1) in vec4 color;

layout(location = 0) out vec4 outColor;

void main()
{
    float alpha = color.a * texture(atlas, uv).a;
    outColor = vec4(pow(color.rgb, vec3(2.2)) * alpha, alpha);
}
