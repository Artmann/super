// Wind for materials with Material.Wind: frame.wind sways their vertices, in the G-buffer and in the
// shadow maps alike, so the shadows follow. Passes/Wind.cs works out the same, and WindTests check
// the two agree. Needs common.glsl for the frame block.

const float WIND_GUST_SPEED = 1.5;     // metres a second the gusts travel downwind
const float WIND_GUST_SIZE = 3.0;      // metres from one gust to the next
const float WIND_FLUTTER = 0.02;       // metres a vertex 1 m up flutters either way
const float WIND_FLUTTER_HZ = 4.0;
const float WIND_FLUTTER_STRENGTH = 0.25; // the wind's strength at which leaves flutter fully
const float WIND_TAU = 6.28318530718;
const float WIND_FULL_HEIGHT = 1.0;    // metres above a part's foot past which a vertex sways no further

// A number from 0 to 1 for a cell, the same as Wind.Hash: a PCG hash of its two coordinates.
float windHash(ivec2 cell) {
    uint state = (uint(cell.x) * 73856093u) ^ (uint(cell.y) * 19349663u);
    state = state * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    word = (word >> 22u) ^ word;
    return float(word) / 4294967295.0;
}

// Smooth value noise from 0 to 1.
float windGust(vec2 at) {
    vec2 cell = floor(at);
    vec2 f = at - cell;
    f = f * f * (3.0 - 2.0 * f);
    ivec2 c = ivec2(cell);
    float bottom = mix(windHash(c), windHash(c + ivec2(1, 0)), f.x);
    float top = mix(windHash(c + ivec2(0, 1)), windHash(c + ivec2(1, 1)), f.x);
    return mix(bottom, top, f.y);
}

// How far a vertex standing at rest at world position p, heightAboveBase metres above its part's lowest
// point, moves in the frame's wind at time, in seconds, for a material whose share of the lean is sway.
// The G-buffer pass also asks at last frame's time, to know where the vertex was for motion vectors.
vec3 windSwayAt(vec3 p, float heightAboveBase, float sway, float time) {
    float strength = length(frame.wind.xyz);
    if (strength <= 0.0 || heightAboveBase <= 0.0) {
        return vec3(0.0);
    }

    vec3 downwind = frame.wind.xyz / strength;
    float h = min(heightAboveBase, WIND_FULL_HEIGHT);
    vec2 at = p.xz / WIND_GUST_SIZE - downwind.xz * (WIND_GUST_SPEED / WIND_GUST_SIZE * time);
    float swing = 0.8 * frame.wind.w;
    float gust = 1.0 - swing + swing * windGust(at);
    float lean = strength * sway * h * h * gust;
    float flutter = WIND_FLUTTER * h * min(strength / WIND_FLUTTER_STRENGTH, 1.0)
        * sin(WIND_TAU * WIND_FLUTTER_HZ * time + dot(p, vec3(3.1, 2.3, 2.7)));
    return downwind * lean + normalize(downwind + vec3(0.0, 1.0, 0.0)) * flutter;
}

// The sway at this frame's time.
vec3 windSway(vec3 p, float heightAboveBase, float sway) {
    return windSwayAt(p, heightAboveBase, sway, frame.time);
}

// Sways a placed vertex at time. windBase is the lowest height of the draw's part in its own space, so the
// part's foot, placed in the world, is where h is 0.
vec3 windSwayPlacedAt(vec3 world, mat4 placed, vec3 posed, float windBase, float sway, float time) {
    if (sway <= 0.0 || (frame.off & OFF_WIND) != 0) {
        return world;
    }

    float base = (placed * vec4(posed.x, windBase, posed.z, 1.0)).y;
    return world + windSwayAt(world, world.y - base, sway, time);
}

// Sways a placed vertex at this frame's time.
vec3 windSwayPlaced(vec3 world, mat4 placed, vec3 posed, float windBase, float sway) {
    return windSwayPlacedAt(world, placed, posed, windBase, sway, frame.time);
}
