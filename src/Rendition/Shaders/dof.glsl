// Shared by the depth of field passes. Needs common.glsl first.

// Must match DepthOfFieldConstants in Passes/DepthOfFieldPass.cs.
layout(push_constant) uniform Lens {
    float focusDistance;    // Metres in front of the camera.
    float pixelsAtInfinity; // The circle's width in scene pixels for a point at the horizon.
    float maxRadius;        // The widest circle's radius, in scene pixels.
    float unused;
} lens;

// How far in front of the camera a pixel is, along its view, or -1 for the sky.
float viewDistance(vec2 uv, float depth) {
    if (depth == 0.0) {
        return -1.0;
    }

    vec4 world = frame.inverseViewProjection * vec4(uv * 2.0 - 1.0, depth, 1.0);
    world /= world.w;
    return -(frame.view * vec4(world.xyz, 1.0)).z;
}

// The thin lens circle's radius in scene pixels: f/N * |d - s| / d * f / (s - f), worked out on the CPU
// up to the |d - s| / d part. Negative in front of the focus, positive behind it.
float circleRadius(vec2 uv, float depth) {
    float metres = viewDistance(uv, depth);
    float away = metres < 0.0 ? 1.0 : (metres - lens.focusDistance) / max(metres, 1e-4);
    return clamp(0.5 * lens.pixelsAtInfinity * away, -lens.maxRadius, lens.maxRadius);
}

// How much of a blurred layer shows for a circle this wide, in scene pixels: none below a quarter of a
// pixel, all from one and a half. A circle that small hardly blurs, so it fades in rather than
// switching the half-size layer on at once.
float layerShare(float radius) {
    return smoothstep(0.25, 1.5, radius);
}
