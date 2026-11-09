#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"

// One decal per instance. Must match GpuDecal in Passes/DecalBatch.cs.
layout(location = 0) in mat4 inBox;     // The cube from -0.5 to 0.5 to the world. Takes locations 0 to 3.
layout(location = 4) in mat4 inInverse; // The world back into the cube. Takes locations 4 to 7.
layout(location = 8) in vec4 inWeights; // Colour, normal and roughness weights, and the angle fade.
layout(location = 9) in uint inMaterial;

layout(location = 0) flat out mat4 outInverse;
layout(location = 4) flat out vec4 outWeights;
layout(location = 5) flat out vec3 outAcross; // The box's X in the world: the material's width.
layout(location = 6) flat out vec3 outUp;     // The box's Y in the world, which the decal projects down.
layout(location = 7) flat out uint outMaterial;
layout(location = 8) flat out vec3 outAlong;  // The box's Z in the world: the way the material's v falls.

// The cube's 36 corners, two triangles a face, wound anticlockwise seen from outside.
// Each number's bits are its x, y and z, from 0 to 1.
const int CORNERS[36] = int[](
    0, 4, 6, 0, 6, 2,  // -x
    1, 3, 7, 1, 7, 5,  // +x
    0, 1, 5, 0, 5, 4,  // -y
    2, 6, 7, 2, 7, 3,  // +y
    0, 2, 3, 0, 3, 1,  // -z
    4, 5, 7, 4, 7, 6); // +z

void main() {
    // A mirrored box turns its triangles inside out, so they're wound the other way round to match.
    int index = gl_VertexIndex;
    if (determinant(mat3(inBox)) < 0.0 && index % 3 != 0) {
        index += index % 3 == 1 ? 1 : -1;
    }

    int corner = CORNERS[index];
    vec3 local = vec3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1) - 0.5;
    gl_Position = frame.viewProjection * (inBox * vec4(local, 1.0));

    outInverse = inInverse;
    outWeights = inWeights;
    outAcross = inBox[0].xyz;
    outUp = inBox[1].xyz;
    outMaterial = inMaterial;
    outAlong = inBox[2].xyz;
}
