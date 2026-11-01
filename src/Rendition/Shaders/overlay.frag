#version 460
#extension GL_EXT_nonuniform_qualifier : require

// The atlas only decides how solid each pixel is. Images bring their own colors, which
// the sampler has already made linear. Colors arrive as people write them, in sRGB, and
// the screen wants them linear.

layout(set = 0, binding = 0) uniform sampler2D atlas;
layout(set = 1, binding = 0) uniform sampler2D textures[];

layout(location = 0) in vec2 uv;
layout(location = 1) in vec4 color;
layout(location = 2) in vec4 borderColor;
layout(location = 3) in vec2 pixel;
layout(location = 4) flat in vec4 quad;
layout(location = 5) flat in vec4 clip;
layout(location = 6) flat in vec3 shape;
layout(location = 7) flat in int textureIndex;

layout(location = 0) out vec4 outColor;

vec3 toLinear(vec3 value)
{
    return pow(value, vec3(2.2));
}

// How far a point is from the edge of a rounded rectangle: negative inside.
float roundedDistance(vec2 point, vec4 rectangle, float radius)
{
    vec2 halfSize = rectangle.zw * 0.5;
    float limit = min(radius, min(halfSize.x, halfSize.y));
    vec2 corner = abs(point - rectangle.xy - halfSize) - halfSize + limit;
    return length(max(corner, 0.0)) + min(max(corner.x, corner.y), 0.0) - limit;
}

// One pixel of antialiasing, centered on the edge.
float inside(float distance)
{
    return clamp(0.5 - distance, 0.0, 1.0);
}

void main()
{
    float radius = shape.x;
    float borderWidth = shape.y;
    float clipRadius = shape.z;

    vec4 fill;
    if (textureIndex == 0)
    {
        float alpha = color.a * texture(atlas, uv).a;
        fill = vec4(toLinear(color.rgb) * alpha, alpha);
    }
    else
    {
        // Textures repeat, so stop short of the edge or filtering pulls in the opposite side.
        int index = nonuniformEXT(textureIndex - 1);
        vec2 edge = 0.5 / vec2(textureSize(textures[index], 0));
        vec4 image = texture(textures[index], clamp(uv, edge, 1.0 - edge));
        float alpha = color.a * image.a;
        fill = vec4(image.rgb * toLinear(color.rgb) * alpha, alpha);
    }

    float distance = roundedDistance(pixel, quad, radius);
    if (borderWidth > 0.0)
    {
        vec4 border = vec4(toLinear(borderColor.rgb) * borderColor.a, borderColor.a);
        fill = mix(fill, border, inside(-distance - borderWidth));
    }

    // Square quads skip the edge, so text and sharp rectangles stay exactly as they were.
    float coverage = radius > 0.0 ? inside(distance) : 1.0;
    if (clip.z > 0.0)
    {
        coverage *= inside(roundedDistance(pixel, clip, clipRadius));
    }

    outColor = fill * coverage;
}
