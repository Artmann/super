#version 460

// One line in the world, drawn as a quad as thick as asked in window pixels. Both ends are cut where the line passes
// behind the camera, so a line that starts behind it still shows the part in front. The ends are carried out by half
// the width, so lines that meet at a corner close it.

layout(location = 0) in vec4 inFrom; // xyz: the start; w: the width in pixels
layout(location = 1) in vec4 inTo;   // xyz: the end; w: how much shows where the scene is in front
layout(location = 2) in vec4 inColor;

layout(push_constant) uniform LineConstants
{
    mat4 viewProjection;
    vec4 screen;
    float perspective;
} line;

layout(location = 0) out vec4 color;
layout(location = 1) out float across;
layout(location = 2) flat out float halfWidth;
layout(location = 3) flat out float hidden;

// x: 0 at the start, 1 at the end; y: which side of the line.
const vec2 Corners[6] = vec2[](
    vec2(0.0, -1.0), vec2(1.0, -1.0), vec2(0.0, 1.0),
    vec2(0.0, 1.0), vec2(1.0, -1.0), vec2(1.0, 1.0));

vec2 toPixels(vec4 clip)
{
    return (clip.xy / clip.w * 0.5 + 0.5) * line.screen.xy;
}

void main()
{
    vec4 a = line.viewProjection * vec4(inFrom.xyz, 1.0);
    vec4 b = line.viewProjection * vec4(inTo.xyz, 1.0);

    // With reversed depth the near plane is where z reaches w, and what is in front of it has z below w.
    float inFrontA = a.w - a.z;
    float inFrontB = b.w - b.z;
    color = inColor;
    halfWidth = inFrom.w * 0.5;
    hidden = inTo.w;
    across = 0.0;
    if (inFrontA <= 0.0 && inFrontB <= 0.0)
    {
        // All of it is behind the camera: every corner in one place covers nothing.
        gl_Position = vec4(0.0, 0.0, 0.5, 1.0);
        return;
    }

    if (inFrontA < 0.0)
    {
        a = mix(a, b, inFrontA / (inFrontA - inFrontB) + 1e-4);
    }

    if (inFrontB < 0.0)
    {
        b = mix(b, a, inFrontB / (inFrontB - inFrontA) + 1e-4);
    }

    vec2 start = toPixels(a);
    vec2 end = toPixels(b);
    vec2 along = end - start;
    float length = length(along);
    vec2 direction = length > 1e-4 ? along / length : vec2(1.0, 0.0);
    vec2 side = vec2(-direction.y, direction.x);

    // A pixel more on each side, for the soft edge.
    float reach = halfWidth + 1.0;
    vec2 corner = Corners[gl_VertexIndex];
    bool atEnd = corner.x > 0.5;
    vec4 clip = atEnd ? b : a;
    vec2 pixel = (atEnd ? end : start) + side * corner.y * reach + direction * (atEnd ? halfWidth : -halfWidth);
    across = corner.y * reach;
    gl_Position = vec4((pixel * line.screen.zw * 2.0 - 1.0) * clip.w, clip.z, clip.w);
}
