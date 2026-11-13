#version 460

// The baker's texel setup: writes the world position, geometric normal and shading normal of the surface under a texel.
// See bake_texels.vert and "Texel setup" in docs/design.md.

// Centroid, so with multisampling a texel whose centre the triangle misses still gets a point on the triangle. With
// conservative rasterization every sample counts as covered and this is the texel's centre, extended along the
// triangle's plane.
layout(location = 0) centroid in vec3 inPosition;
layout(location = 1) centroid in vec3 inNormal;
// The same position at the texel's centre, which changes evenly across the texels, for the triangle's slope.
layout(location = 2) in vec3 inPlanePosition;
// The number of the rectangle this draw was drawn for, from 1. A texel conservative drawing spills into the gutter
// still takes its own rectangle's number.
layout(location = 3) flat in float inChart;

// w is the rectangle's number, so above 0 marks a covered texel; the targets are cleared to 0.
layout(location = 0) out vec4 outPosition;
layout(location = 1) out vec4 outGeometricNormal;
layout(location = 2) out vec4 outShadingNormal;

void main() {
    float shadingLength = length(inNormal);
    vec3 shading = shadingLength > 1e-12 ? inNormal / shadingLength : vec3(0.0, 0.0, 1.0);

    // The triangle's own normal, from how the position changes across texels. Which way it points depends on how the
    // second UVs wind, which can be mirrored, so it takes the side the vertex normals face.
    vec3 face = cross(dFdx(inPlanePosition), dFdy(inPlanePosition));
    float faceLength = length(face);
    vec3 geometric = faceLength > 1e-20 ? face / faceLength : shading;
    if (dot(geometric, shading) < 0.0) {
        geometric = -geometric;
    }

    outPosition = vec4(inPosition, inChart);
    outGeometricNormal = vec4(geometric, 1.0);
    outShadingNormal = vec4(shading, 1.0);
}
