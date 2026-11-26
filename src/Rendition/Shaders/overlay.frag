#version 460
#extension GL_EXT_nonuniform_qualifier : require

// The atlas only decides how solid each pixel is. Images bring their own colors, which
// the sampler has already made linear. Colors arrive as people write them, in sRGB, and
// the screen wants them linear. Gradients mix in sRGB, as CSS does.

layout(set = 0, binding = 0) uniform sampler2D atlas;
layout(set = 1, binding = 0) uniform sampler2D textures[];

layout(location = 0) in vec2 uv;
layout(location = 1) in vec4 color;
layout(location = 2) in vec4 color2;
layout(location = 3) in vec4 borderColor;
layout(location = 4) in vec2 pixel;
layout(location = 5) flat in vec4 quad;
layout(location = 6) flat in vec4 clip;
layout(location = 7) flat in vec4 radii;
layout(location = 8) flat in vec4 clipRadii;
layout(location = 9) flat in vec4 gradientPoints;
layout(location = 10) flat in vec4 params;
layout(location = 11) flat in vec2 widths;
layout(location = 12) flat in ivec3 kinds;

layout(location = 0) out vec4 outColor;

const int Box = 0;
const int Ellipse = 1;
const int Arc = 2;
const int Segment = 3;
const int Shadow = 4;

const int Linear = 1;
const int Radial = 2;

const float Tau = 6.28318530718;

vec3 toLinear(vec3 value)
{
    return pow(value, vec3(2.2));
}

// How far a point is from the edge of a rectangle whose corners are rounded by
// top left, top right, bottom right and bottom left in turn: negative inside.
float roundedDistance(vec2 point, vec4 rectangle, vec4 corners)
{
    vec2 halfSize = rectangle.zw * 0.5;
    vec2 local = point - rectangle.xy - halfSize;
    vec2 side = local.x > 0.0 ? corners.yz : corners.xw;
    float radius = min(local.y > 0.0 ? side.y : side.x, min(halfSize.x, halfSize.y));
    vec2 corner = abs(local) - halfSize + radius;
    return length(max(corner, 0.0)) + min(max(corner.x, corner.y), 0.0) - radius;
}

// Close to the distance from the edge of the ellipse filling the rectangle, which is all the edge needs.
float ellipseDistance(vec2 point, vec4 rectangle)
{
    vec2 axes = max(rectangle.zw * 0.5, vec2(0.001));
    vec2 local = point - rectangle.xy - axes;
    float k0 = length(local / axes);
    float k1 = length(local / (axes * axes));
    return k1 > 0.0 ? k0 * (k0 - 1.0) / k1 : -min(axes.x, axes.y);
}

// Part of a ring with round ends, centered in the rectangle, from start through sweep, clockwise from the top.
float arcDistance(vec2 point, vec4 rectangle, float start, float sweep, float thickness)
{
    vec2 center = rectangle.xy + rectangle.zw * 0.5;
    float radius = min(rectangle.z, rectangle.w) * 0.5 - thickness * 0.5;
    vec2 local = point - center;
    float halfThickness = thickness * 0.5;
    if (sweep <= 0.0)
    {
        return 1e6;
    }

    float angle = atan(local.x, -local.y);
    float along = mod(angle - start, Tau);
    if (sweep >= Tau || along <= sweep)
    {
        return abs(length(local) - radius) - halfThickness;
    }

    vec2 first = radius * vec2(sin(start), -cos(start));
    vec2 last = radius * vec2(sin(start + sweep), -cos(start + sweep));
    return min(length(local - first), length(local - last)) - halfThickness;
}

float segmentDistance(vec2 point, vec2 from, vec2 to, float thickness)
{
    vec2 line = to - from;
    float along = clamp(dot(point - from, line) / max(dot(line, line), 0.0001), 0.0, 1.0);
    return length(point - from - line * along) - thickness * 0.5;
}

// The error function, near enough for a shadow (Abramowitz and Stegun).
float erf(float x)
{
    float squared = x * x;
    const float a = 0.147;
    float value = sqrt(1.0 - exp(-squared * (1.2732395 + a * squared) / (1.0 + a * squared)));
    return x < 0.0 ? -value : value;
}

// One pixel of antialiasing, centered on the edge.
float inside(float distance)
{
    return clamp(0.5 - distance, 0.0, 1.0);
}

// How much of a pixel a character's edge covers, for text of this sRGB colour. Edges blend in linear
// light, which draws dark text on a light ground thin and faint. Dark text gets the coverage that
// blending in sRGB would give it on white, and light text, which already looks right, keeps its own.
float glyphCoverage(float coverage, vec3 textColor)
{
    float lightness = dot(textColor, vec3(0.2126, 0.7152, 0.0722));
    return mix(1.0 - pow(1.0 - coverage, 2.2), coverage, lightness);
}

// The fill's colour here, in sRGB with straight alpha.
vec4 paint()
{
    if (kinds.z == 0)
    {
        return color;
    }

    vec2 local = pixel - quad.xy;
    float t;
    if (kinds.z == Linear)
    {
        vec2 line = gradientPoints.zw - gradientPoints.xy;
        t = dot(local - gradientPoints.xy, line) / max(dot(line, line), 0.0001);
    }
    else
    {
        t = length(local - gradientPoints.xy) / max(gradientPoints.z, 0.0001);
    }

    // Mixed premultiplied, so a gradient into a clear colour doesn't turn grey on the way.
    t = clamp(t, 0.0, 1.0);
    float alpha = mix(color.a, color2.a, t);
    vec3 premultiplied = mix(color.rgb * color.a, color2.rgb * color2.a, t);
    return vec4(alpha > 0.0 ? premultiplied / alpha : vec3(0.0), alpha);
}

void main()
{
    int kind = kinds.y;
    int textureIndex = kinds.x;
    float borderWidth = widths.x;
    float thickness = widths.y;
    vec4 base = paint();

    vec4 fill;
    if (textureIndex != 0)
    {
        // Textures repeat, so stop short of the edge or filtering pulls in the opposite side.
        int index = nonuniformEXT(textureIndex - 1);
        vec2 edge = 0.5 / vec2(textureSize(textures[index], 0));
        vec4 image = texture(textures[index], clamp(uv, edge, 1.0 - edge));
        float alpha = base.a * image.a;
        fill = vec4(image.rgb * toLinear(base.rgb) * alpha, alpha);
    }
    else
    {
        float alpha = kind == Box ? base.a * glyphCoverage(texture(atlas, uv).a, base.rgb) : base.a;
        fill = vec4(toLinear(base.rgb) * alpha, alpha);
    }

    float coverage;
    if (kind == Shadow)
    {
        // A box blurred by a Gaussian, near enough: the edge's distance through the error function.
        // The box casting it is cut out, so the shadow doesn't show through a see-through box. The cut
        // stops a pixel inside its edge: the box only partly covers the pixels on its edge, and a shadow
        // cut away there too leaves a dark seam round every curve.
        float distance = roundedDistance(pixel, params, radii);
        float sigma = thickness * 0.5;
        coverage = sigma > 0.0 ? 0.5 - 0.5 * erf(distance / (sigma * 1.41421356)) : inside(distance);
        if (gradientPoints.z > 0.0)
        {
            coverage *= 1.0 - inside(roundedDistance(pixel, gradientPoints, radii) + 1.0);
        }
    }
    else if (kind == Arc)
    {
        coverage = inside(arcDistance(pixel, quad, params.x, params.y, thickness));
    }
    else if (kind == Segment)
    {
        coverage = inside(segmentDistance(pixel, quad.xy + params.xy, quad.xy + params.zw, thickness));
    }
    else
    {
        float distance = kind == Ellipse ? ellipseDistance(pixel, quad) : roundedDistance(pixel, quad, radii);
        if (borderWidth > 0.0)
        {
            vec4 border = vec4(toLinear(borderColor.rgb) * borderColor.a, borderColor.a);
            fill = mix(fill, border, inside(-distance - borderWidth));
        }

        // Square boxes skip the edge, so text and sharp rectangles stay exactly as they were.
        bool square = kind == Box && max(max(radii.x, radii.y), max(radii.z, radii.w)) <= 0.0;
        coverage = square ? 1.0 : inside(distance);
    }

    if (clip.z > 0.0)
    {
        coverage *= inside(roundedDistance(pixel, clip, clipRadii));
    }

    outColor = fill * coverage;
}
