#version 460
#extension GL_GOOGLE_include_directive : require
#include "grid_overlay.glsl"

// One grid overlay per instance, drawn as the cube round its cells. Only the far faces are drawn, so each pixel is
// shaded once, even with the camera inside the box.

layout(location = 0) flat out int outOverlay;

// The cube's 36 corners, two triangles a face, wound anticlockwise seen from outside, as decal.vert draws them.
// Each number's bits are its x, y and z, from 0 to 1.
const int CORNERS[36] = int[](
    0, 4, 6, 0, 6, 2,  // -x
    1, 3, 7, 1, 7, 5,  // +x
    0, 1, 5, 0, 5, 4,  // -y
    2, 6, 7, 2, 7, 3,  // +y
    0, 2, 3, 0, 3, 1,  // -z
    4, 5, 7, 4, 7, 6); // +z

void main() {
    int corner = CORNERS[gl_VertexIndex];
    vec3 local = vec3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1) - 0.5;
    outOverlay = gl_InstanceIndex;
    gl_Position = overlays[gl_InstanceIndex].toClip * vec4(local, 1.0);
}
