// The grade's table: the colour matrix, contrast, saturation, curve, gain, lift, tones, split
// toning and the artist's table, baked by grade_lut.comp into a cube the tone-mapping pass reads
// once a pixel. Linear light is too wide a range for a cube's few cells, so each axis is the log2
// of the light: GRADE_LUT_FLOOR keeps black on the first cell, and the last is 2^8 times white,
// past which the light is held. See "The grade's table" in docs/design.md.
// Must match GradeLut.Size in Vulkan/GradeLut.cs.
const float GRADE_LUT_SIZE = 64.0;
const float GRADE_LUT_FLOOR = 1.0 / 1024.0;
const float GRADE_LUT_LOG_MIN = -10.0;
const float GRADE_LUT_LOG_MAX = 8.0000056; // log2(256 + 1/1024)

// From linear light to 0 to 1 along an axis of the table.
vec3 gradeLutEncode(vec3 light) {
    vec3 u = (log2(max(light, vec3(0.0)) + GRADE_LUT_FLOOR) - GRADE_LUT_LOG_MIN) / (GRADE_LUT_LOG_MAX - GRADE_LUT_LOG_MIN);
    return clamp(u, 0.0, 1.0);
}

// And back: the light a cell stands for.
vec3 gradeLutDecode(vec3 u) {
    return exp2(GRADE_LUT_LOG_MIN + u * (GRADE_LUT_LOG_MAX - GRADE_LUT_LOG_MIN)) - GRADE_LUT_FLOOR;
}

// Where along a table of this many cells to sample so 0 and 1 land on the middles of its first
// and last cells, as the cells were baked.
vec3 lutCoordinate(vec3 u, float size) {
    return u * ((size - 1.0) / size) + 0.5 / size;
}
