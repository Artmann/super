// Shared by the paint passes. Needs common.glsl first. See "Paint" in docs/design.md.

// Must match PaintConstants in Passes/PaintPass.cs.
layout(push_constant) uniform Paint {
    float amount;   // How much of the painted picture shows, 0 to 1.
    float near;     // Metres in front of the camera where the paint starts fading in.
    float far;      // Metres where it reaches the full amount; 0 paints the whole picture.
    float radius;   // The brush's radius in half-size pixels, before it stretches along an edge.
    float hardness; // What a sector's variance is multiplied by before it is weighed: higher keeps edges harder.
    float pad0;
    float pad1;
    float pad2;
} paint;

// Below this much paint a pixel is left exactly as it was.
const float PAINT_CUT = 0.02;

// How far in front of the camera a pixel is, along its view, or -1 for the sky.
float paintDistance(vec2 uv, float depth) {
    if (depth == 0.0) {
        return -1.0;
    }

    vec4 world = frame.inverseViewProjection * vec4(uv * 2.0 - 1.0, depth, 1.0);
    world /= world.w;
    return -(frame.view * vec4(world.xyz, 1.0)).z;
}

// How much paint a pixel at this depth gets: the amount over the whole picture, or faded in between near and far.
// The sky counts as far.
float paintMask(vec2 uv, float depth) {
    if (paint.far <= 0.0 || depth == 0.0) {
        return paint.amount;
    }

    return paint.amount * smoothstep(paint.near, paint.far, paintDistance(uv, depth));
}
