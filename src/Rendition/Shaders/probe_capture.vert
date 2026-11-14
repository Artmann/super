#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_multiview : require
#include "common.glsl"
#include "instances.glsl"

// Draws the scene round one light probe into the six faces of its cube at once: multiview runs
// this once per face, with gl_ViewIndex as the face. Things are drawn as they stand, with no
// skin, swim or wind. See Passes/ProbeCapturePass.cs.

layout(location = 0) in vec3 inPosition;
layout(location = 1) in vec3 inNormal;
layout(location = 3) in vec2 inUv;
// The tangent (2), joints, weights and second UV (4 to 6) are left out: nothing here bends a normal or moves a vertex.

// Must match DrawConstants in Passes/SceneDraws.cs, then ProbeConstants in Passes/ProbeCapturePass.cs.
layout(push_constant) uniform Draw {
    vec4 tint;
    vec4 ghost;
    int material;
    int cascade;
    int firstBone;
    float windBase;
    float windSway;
    vec4 probe; // xyz: where the probe is
} draw;

layout(location = 0) out vec3 outNormal;
layout(location = 1) out vec2 outUv;
layout(location = 2) out vec3 outPosition;

// The camera behind each face: which way it looks and which way is up. A cube map is mirrored
// compared with a camera, so z swaps here and probe_fold.comp swaps it back. The same faces as
// reflection probes use.
const vec3 FACE_FORWARD[6] = vec3[6](
    vec3(1.0, 0.0, 0.0), vec3(-1.0, 0.0, 0.0), vec3(0.0, 1.0, 0.0),
    vec3(0.0, -1.0, 0.0), vec3(0.0, 0.0, -1.0), vec3(0.0, 0.0, 1.0));
const vec3 FACE_UP[6] = vec3[6](
    vec3(0.0, 1.0, 0.0), vec3(0.0, 1.0, 0.0), vec3(0.0, 0.0, 1.0),
    vec3(0.0, 0.0, -1.0), vec3(0.0, 1.0, 0.0), vec3(0.0, 1.0, 0.0));

// Where the probe's view starts. Nearer than this, a surface is cut away, so it is kept to 1 mm:
// a probe often sits a few centimetres from a wall whose plane lies near the probe grid, and a
// larger near plane let it see through that wall. The traced path's rays start at the probe.
// Depth is reversed-Z in 32-bit float, which stores near / distance, so its relative precision
// is the same whatever this is.
const float CAPTURE_NEAR = 0.001;

void main() {
    mat4 placed = instanceModel();
    mat3 model = mat3(placed);
    mat3 cofactor = mat3(cross(model[1], model[2]), cross(model[2], model[0]), cross(model[0], model[1]));
    outNormal = cofactor * inNormal * sign(dot(model[0], cofactor[0]));
    outUv = inUv;
    vec3 world = (placed * vec4(inPosition, 1.0)).xyz;
    outPosition = world;

    // A look-at view and a 90 degree reversed-Z projection with no far plane, as
    // Matrix4x4.CreateLookAt and Projection.PerspectiveInfiniteReversedZ build them.
    vec3 forward = FACE_FORWARD[gl_ViewIndex];
    vec3 right = normalize(cross(FACE_UP[gl_ViewIndex], -forward));
    vec3 up = cross(-forward, right);
    vec3 d = world - draw.probe.xyz;
    gl_Position = vec4(dot(right, d), -dot(up, d), CAPTURE_NEAR, dot(forward, d));
}
