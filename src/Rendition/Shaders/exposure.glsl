// Auto exposure's histogram and the exposure it settles on, kept on the GPU from frame to frame
// so nothing is read back. Must match Passes/Exposure.cs and Vulkan/ExposureMeter.cs.
// See "Auto exposure" in docs/design.md.

const int EXPOSURE_BINS = 64;
const float EXPOSURE_MIN_EV = -8.0;
const float EXPOSURE_MAX_EV = 16.0;
const float EXPOSURE_LOW_CUT = 0.4;
const float EXPOSURE_HIGH_CUT = 0.95;

#ifndef EXPOSURE_SET
#define EXPOSURE_SET 0
#endif

// The tonemap reads it as readonly, since fragment shaders may not write storage buffers here.
#ifndef EXPOSURE_ACCESS
#define EXPOSURE_ACCESS
#endif

layout(std430, set = EXPOSURE_SET, binding = 0) EXPOSURE_ACCESS buffer ExposureState {
    uint bins[EXPOSURE_BINS]; // This frame's pixels in each bin, emptied again once read.
    float adaptedEv;          // The EV the picture is exposed for, carried from frame to frame.
    float pad;
} exposure;
