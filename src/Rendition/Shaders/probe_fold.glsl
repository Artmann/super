// The sums that turn what a light probe saw into its two tiles, shared by every way of capturing
// one. The irradiance tile holds, for each direction a surface could face, the light reaching it:
// radiance averaged over the half of the sphere in front, weighted by the cosine. The depth tile
// holds the mean and the mean square of the distance seen around each direction, which probes.glsl
// uses to tell whether a point is hidden from the probe. Both are octahedral maps with a one-texel
// border copied from across each edge, so filtering wraps round. A fold writes its tiles to the
// staging buffer, and probe_blend.comp blends them into the atlases.
// Needs sampling.glsl first. See "Light probes" in docs/design.md.
#ifndef PROBE_FOLD_GLSL
#define PROBE_FOLD_GLSL

// Must match ProbeVolume.cs and probes.glsl.
const int IRRADIANCE_TEXELS = 8;
const int DEPTH_TEXELS = 16;
const int IRRADIANCE_SIDE = IRRADIANCE_TEXELS + 2;
const int DEPTH_SIDE = DEPTH_TEXELS + 2;

// How tightly the depth gathers round its direction. High, so a wall's edge stays sharp.
const float DEPTH_SHARPNESS = 50.0;

// One probe's two bordered tiles, texel x + side * y, as the staging buffer holds them. 4,192 bytes
// under std430. Must match ProbeVolume.StagedTileBytes.
struct StagedTile {
    vec4 irradiance[IRRADIANCE_SIDE * IRRADIANCE_SIDE];
    vec2 depth[DEPTH_SIDE * DEPTH_SIDE];
};

// The texel inside an n by n tile that a texel of the bordered tile shows. A border texel
// shows its neighbour across the octahedral map's edge, which is mirrored; a corner shows the
// opposite corner.
ivec2 interiorOf(ivec2 b, int n) {
    bool sideX = b.x == 0 || b.x == n + 1;
    bool sideY = b.y == 0 || b.y == n + 1;
    if (sideX && sideY) {
        return ivec2(b.x == 0 ? n - 1 : 0, b.y == 0 ? n - 1 : 0);
    }

    if (sideY) {
        return ivec2(n - b.x, b.y == 0 ? 0 : n - 1);
    }

    if (sideX) {
        return ivec2(b.x == 0 ? 0 : n - 1, n - b.y);
    }

    return b - 1;
}

// The direction a texel of a bordered n by n tile stands for.
vec3 texelDirection(ivec2 bordered, int n) {
    vec2 e = (vec2(interiorOf(bordered, n)) + 0.5) / float(n) * 2.0 - 1.0;
    return octahedralDecode(e);
}

// How much a sample seen along d counts towards an irradiance texel facing direction: the cosine,
// times the share of the sphere the sample covers.
float irradianceWeight(vec3 direction, vec3 d, float solidAngle) {
    return max(dot(direction, d), 0.0) * solidAngle;
}

// How much a sample seen along d counts towards a depth texel looking along direction.
float depthWeight(vec3 direction, vec3 d, float solidAngle) {
    return pow(max(dot(direction, d), 0.0), DEPTH_SHARPNESS) * solidAngle;
}

// Inside a wall or under the floor a probe sees mostly the backs of surfaces, and its light would
// leak through. Over half of what it saw being backs counts as inside.
bool probeSawInside(uint backFaces, uint seen) {
    return backFaces * 2u > seen;
}

// The depth tile's first corner marks a probe inside a wall, and probes.glsl skips it.
vec2 markInside(vec2 moments, int bordered, bool inside) {
    if (inside && bordered == 0) {
        moments.y = -1.0;
    }

    return moments;
}

#endif
