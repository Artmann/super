#version 460

// Colours arrive as people write them, in sRGB, and the screen wants them linear. Where the scene's depth is nearer
// than the line, only the hidden share of it shows. The scene's depth is read at this pixel's place on the screen, so
// it may be smaller than the window.

layout(set = 0, binding = 0) uniform sampler2D sceneDepth;

layout(push_constant) uniform LineConstants
{
    mat4 viewProjection;
    vec4 screen;
    float perspective;
} line;

layout(location = 0) in vec4 color;
layout(location = 1) in float across;
layout(location = 2) flat in float halfWidth;
layout(location = 3) flat in float hidden;

layout(location = 0) out vec4 outColor;

void main()
{
    // One pixel of antialiasing, centred on the edge.
    float coverage = clamp(halfWidth + 0.5 - abs(across), 0.0, 1.0);

    // Reversed depth: nearer is larger. A little slack keeps a line drawn on a surface from flickering into it: a
    // share of the distance with perspective, about two centimetres without.
    float scene = texture(sceneDepth, gl_FragCoord.xy * line.screen.zw).r;
    float mine = gl_FragCoord.z;
    float slack = line.perspective > 0.5 ? mine * 0.004 : 2e-6;
    float shown = scene > mine + slack ? hidden : 1.0;

    float alpha = color.a * coverage * shown;
    outColor = vec4(pow(color.rgb, vec3(2.2)) * alpha, alpha);
}
