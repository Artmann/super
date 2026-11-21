// The special values a G-buffer shader writes, shared by every shader that fills the G-buffer: surface.glsl and
// grass.frag. The lighting pass reads them back, so they must not differ between the two.

// The motion of a point that was behind last frame's camera: far off any picture, near the most a 16-bit float holds.
const float MOTION_OFF_PICTURE = -60000.0;

// Foliage writes its translucency as a negative number, and at least this much, so foliage that lets no light through
// is still marked as foliage.
const float FOLIAGE_MARK = 1.0 / 1024.0;

// What foliage writes in the clearcoat channel: its translucency, 0 to 1, as a negative number.
float foliageChannel(float translucency) {
    return -max(translucency, FOLIAGE_MARK);
}
