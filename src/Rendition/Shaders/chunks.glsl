// The frame's placed chunks, which relight every copy of a chunk's bake by the frame's sky and suns, and the sky's
// light as L1 spherical harmonics. Each vec4 of coefficients is in ShL1's order (Baking/ShL1.cs): constant, then y, z,
// x. Filled by the renderer each frame a chunk's bake is drawn; see "Chunk transfer" in docs/design.md.

// The most placed chunks a frame may name. Must match BakeFiles.MaxChunks.
const int MAX_CHUNKS = 256;

// One placed chunk. Must match GpuChunk in Renderer.cs.
struct ChunkData {
    // From the world into the chunk's own space: vec4(world, 1) * intoChunk is the point in the chunk, and
    // vec4(direction, 0) * intoChunk a direction. A chunk only turns and moves, so this is a rotation and a move.
    mat3x4 intoChunk;
    vec4 min;          // xyz: the chunk's box in its own space
    vec4 max;
    ivec4 maskSlots;   // for each of the frame's four mask slots, the channel of the bake's mask that holds it, or -1
    ivec4 transfer;    // bindless slots of the sky's red, green and blue transfer pages, then the sun's
    ivec4 pages;       // x: the bindless slot of the sun transfer's tint page; y: the shadow mask page's, or -1
};

layout(std430, set = 0, binding = 11) readonly buffer Chunks {
    // The frame's Mixed and Baked suns summed as deltas, each its radiance times the basis towards it: coefficient k's
    // red, green and blue in sun[k].rgb. In world space.
    vec4 sun[4];
    // For each of the frame's four mask slots, the channel of the space bake's mask that holds it: the slot itself, or
    // -1 where a chunk's light took it. Written every frame, chunks or none.
    ivec4 spaceSlots;
    ChunkData chunks[MAX_CHUNKS];
} chunkData;

// The sky cube projected by sky_sh.comp: coefficient k's red, green and blue in rgb and its luminance in a, unscaled by
// the environment's intensity. In world space.
layout(std430, set = 0, binding = 12) readonly buffer SkySh {
    vec4 coefficients[4];
} skySh;

// A direction or an L1 set's vector part, turned from the world into chunk c's space.
vec3 intoChunkDirection(int c, vec3 direction) {
    return vec4(direction, 0.0) * chunkData.chunks[c].intoChunk;
}

// A direction turned from chunk c's space back into the world: the transpose, since the turn is a rotation.
vec3 outOfChunkDirection(int c, vec3 direction) {
    mat3x4 m = chunkData.chunks[c].intoChunk;
    return mat3(m[0].xyz, m[1].xyz, m[2].xyz) * direction;
}

// An L1 set turned from the world into chunk c's space: its vector part (x, y, z) = (C3, C1, C2) turns as a direction
// does, as ShL1.Rotate.
vec4 intoChunkSh(int c, vec4 sh) {
    vec3 v = intoChunkDirection(c, vec3(sh.w, sh.y, sh.z));
    return vec4(sh.x, v.y, v.z, v.x);
}
