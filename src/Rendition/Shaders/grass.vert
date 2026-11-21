#version 460
#extension GL_GOOGLE_include_directive : require
// One vertex of a blade of grass into the G-buffer, drawn once for each blade the scatter pass left.
#include "common.glsl"
#include "wind.glsl"
#include "grass.glsl"

layout(location = 0) out vec3 outNormal;
layout(location = 1) out float outUp;
layout(location = 2) flat out vec2 outLook; // x: the blade's shade, -1 to 1; y: its look
layout(location = 3) out vec3 outClipNow;    // clip x, y and w, without the anti-aliasing shift; see gbuffer.vert
layout(location = 4) out vec3 outClipBefore; // the same, last frame

void main() {
    GrassBlade blade = blades[gl_InstanceIndex];
    BladePoint point = bladePoint(blade, gl_VertexIndex, frame.time);
    outNormal = point.normal;
    outUp = point.up;
    outLook = blade.look.zw;
    gl_Position = frame.viewProjection * vec4(point.position, 1.0);
    outClipNow = vec3(gl_Position.xy - frame.finish.zw * gl_Position.w, gl_Position.w);
    vec3 before = bladePoint(blade, gl_VertexIndex, frame.previousTime).position;
    outClipBefore = (frame.previousViewProjection * vec4(before, 1.0)).xyw;
}
