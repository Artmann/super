// Point and spot lights, sorted into clusters: the view cut into CLUSTER_X by CLUSTER_Y
// tiles on screen and CLUSTER_Z slices of depth, each listing the lights that reach it.
// cluster.comp fills the lists; the lighting, glass and particle passes read them.
// Needs common.glsl. The sizes must match LocalLights in Passes/LocalLights.cs.

const uint CLUSTER_X = 16;
const uint CLUSTER_Y = 9;
const uint CLUSTER_Z = 24;
const uint CLUSTER_COUNT = CLUSTER_X * CLUSTER_Y * CLUSTER_Z;
const uint CLUSTER_LIGHTS = 256; // The most one cluster lists; two to a word.

// Must match GpuLocalLight in Passes/FrameUniforms.cs.
struct LocalLight {
    vec4 positionRange;  // xyz: where it is; w: how far it reaches
    vec4 radianceShadow; // rgb: colour times brightness at one metre; w: first shadow tile, or -1
    vec4 directionCone;  // xyz: which way a spot shines; w: cosine of its outer angle, or -2 for a point light
    vec4 fade;           // x: 1 / (cos inner - cos outer); y: the light's radius; z: how strongly it lights the volumetric fog;
                         // w: 1 when it casts a shadow, with a tile or not, else 0
};

// Must match GpuShadowTile in Passes/FrameUniforms.cs.
struct ShadowTile {
    mat4 matrix; // world to the tile's clip space, reversed-Z
    vec4 rect;   // xy: corner in the atlas; zw: size, as fractions of it
    vec4 depth;  // x, y: near and far planes; z: 1 / tan of half the view
};

layout(std430, set = 0, binding = 3) readonly buffer LocalLights {
    LocalLight localLights[];
};

layout(std430, set = 0, binding = 4) readonly buffer ShadowTiles {
    ShadowTile shadowTiles[];
};

#ifndef CLUSTER_WRITER
layout(std430, set = 0, binding = 5) readonly buffer Clusters {
    uint clusterCounts[CLUSTER_COUNT];
    uint clusterLights[]; // CLUSTER_LIGHTS / 2 words per cluster, two 16-bit light indices to a word
};
#endif

// The slice a depth in front of the camera falls in. Under perspective the slices grow in
// step with depth, so near ones are thin; a parallel view's are all alike.
uint clusterSlice(float viewDepth) {
    float nearest = frame.clusters.x;
    float furthest = frame.clusters.y;
    float t = frame.clusters.z > 0.5
        ? (viewDepth - nearest) / (furthest - nearest)
        : log(max(viewDepth, 1e-4) / nearest) / log(furthest / nearest);
    return uint(clamp(t * float(CLUSTER_Z), 0.0, float(CLUSTER_Z) - 1.0));
}

// Which cluster a pixel at a point falls in.
uint clusterAt(vec2 fragCoord, vec3 position) {
    float viewDepth = -(frame.view * vec4(position, 1.0)).z;
    uvec2 tile = min(uvec2(fragCoord / frame.screen.xy * vec2(CLUSTER_X, CLUSTER_Y)), uvec2(CLUSTER_X - 1, CLUSTER_Y - 1));
    return (clusterSlice(viewDepth) * CLUSTER_Y + tile.y) * CLUSTER_X + tile.x;
}

#ifndef CLUSTER_WRITER
uint clusterLightCount(uint cluster) {
    return frame.localLightCount > 0 ? clusterCounts[cluster] : 0u;
}

uint clusterLight(uint cluster, uint i) {
    uint word = clusterLights[cluster * (CLUSTER_LIGHTS / 2) + i / 2];
    return (word >> (16u * (i & 1u))) & 0xFFFFu;
}
#endif

// How much of a light reaches a point, and the unit vector towards it. It dims with the
// square of the distance, and a window takes it smoothly to nothing at its range. A spot
// fades between its inner and outer angles.
//
// The light has a radius, as a flame or a bulb has a size, so it can't get brighter than
// about 1 / radius² however close a surface is: (1 + r²) / (d² + r²). That is 1 at a
// metre whatever the radius, so a bigger light calms a wall beside it without dimming
// the room.
float localLightReaching(LocalLight light, vec3 position, out vec3 l) {
    vec3 toLight = light.positionRange.xyz - position;
    float d2 = dot(toLight, toLight);
    float range = light.positionRange.w;
    float r2 = light.fade.y * light.fade.y;
    l = toLight * inversesqrt(max(d2, 1e-8));
    float edge = d2 / (range * range);
    float window = clamp(1.0 - edge * edge, 0.0, 1.0);
    float reaching = window * window * (1.0 + r2) / (d2 + r2);

    float cosOuter = light.directionCone.w;
    if (cosOuter > -1.5) {
        float t = clamp((dot(-l, light.directionCone.xyz) - cosOuter) * light.fade.x, 0.0, 1.0);
        reaching *= t * t;
    }

    return reaching;
}
