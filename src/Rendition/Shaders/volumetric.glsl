// Volumetric fog: the fog near the camera, worked out in a grid of cells that follows the
// view, one cell for every 8 by 8 pixels and VOLUMETRIC_SLICES slices deep, out to
// frame.volumetric.x metres of view depth. volumetric_inject.comp lights each cell and
// volumetric_integrate.comp sums each column front to back; the lookup here reads the sum.
// Needs common.glsl. The slices and sums must match Passes/VolumetricFog.cs, which the tests check.
// A pass that writes the volume defines VOLUMETRIC_WRITER, which leaves the lookup out.
#ifndef VOLUMETRIC_GLSL
#define VOLUMETRIC_GLSL

const int VOLUMETRIC_SLICES = 64;
const float VOLUMETRIC_HISTORY = 0.9; // Of each cell's light, the share from last frame's, reprojected.

// The view depth of slice boundary i: frame.volumetric.y at 0 and frame.volumetric.x at
// VOLUMETRIC_SLICES, each slice the same ratio deeper than the last.
float volumetricSliceDepth(float slice) {
    float near = frame.volumetric.y;
    return near * pow(frame.volumetric.x / near, slice / float(VOLUMETRIC_SLICES));
}

// The slice boundary, with a fraction, at a view depth.
float volumetricSliceOf(float depth) {
    float near = frame.volumetric.y;
    return log(depth / near) / log(frame.volumetric.x / near) * float(VOLUMETRIC_SLICES);
}

// Where cell i starts, as view depth: cell 0 starts at the camera and ends at boundary 1,
// and every other cell runs from its own boundary to the next.
float volumetricCellStart(int cell) {
    return cell <= 0 ? 0.0 : volumetricSliceDepth(float(cell));
}

// How far through the cells a view depth is: cell i covers i to i + 1.
float volumetricCellOf(float depth) {
    float first = volumetricSliceDepth(1.0);
    return depth < first ? depth / first : volumetricSliceOf(depth);
}

float volumetricViewDepth(vec3 position) {
    return -(frame.view * vec4(position, 1.0)).z;
}

// The view's ray through a point of the screen: the point on it at view depth d is
// origin + along * d. Two points on the ray, unprojected at two depths, give the rest, which
// suits a perspective and a parallel view alike.
void volumetricRay(vec2 uv, out vec3 origin, out vec3 along) {
    vec4 a = frame.inverseViewProjection * vec4(uv * 2.0 - 1.0, 1.0, 1.0);
    vec4 b = frame.inverseViewProjection * vec4(uv * 2.0 - 1.0, 0.5, 1.0);
    vec3 nearPoint = a.xyz / a.w;
    vec3 farPoint = b.xyz / b.w;
    float nearDepth = volumetricViewDepth(nearPoint);
    float farDepth = volumetricViewDepth(farPoint);
    along = (farPoint - nearPoint) / (farDepth - nearDepth);
    origin = nearPoint - along * nearDepth;
}

// Henyey-Greenstein: the share of light, per steradian, turned by an angle whose cosine is
// cosTheta, 1 straight on. Positive g scatters onwards.
float volumetricPhase(float cosTheta, float g) {
    float g2 = g * g;
    float d = 1.0 + g2 - 2.0 * g * cosTheta;
    return (1.0 - g2) / (12.5663706 * d * sqrt(d));
}

// Where a point is on the screen, as 0 to 1.
vec2 volumetricScreenUv(vec3 position) {
    vec4 clip = frame.viewProjection * vec4(position, 1.0);
    return clip.xy / clip.w * 0.5 + 0.5;
}

#ifndef VOLUMETRIC_WRITER
layout(set = 0, binding = 8) uniform sampler3D volumetricFogTexture;

// The fog's light in front of a view depth on the ray through uv in rgb, and the share of
// what's behind that gets through in a, from the integrated volume. Texel i holds the sum
// to the far side of cell i, and in front of the first texel the sum fades to nothing.
vec4 volumetricLookup(vec2 uv, float depth) {
    float cell = volumetricCellOf(clamp(depth, 0.0, frame.volumetric.x));
    if (cell < 1.0) {
        vec4 first = textureLod(volumetricFogTexture, vec3(uv, 0.5 / float(VOLUMETRIC_SLICES)), 0.0);
        return mix(vec4(0.0, 0.0, 0.0, 1.0), first, cell);
    }

    return textureLod(volumetricFogTexture, vec3(uv, (cell - 0.5) / float(VOLUMETRIC_SLICES)), 0.0);
}
#endif

#endif
