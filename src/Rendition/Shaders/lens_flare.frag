#version 460

// The shape of one flare element, from -1 to 1 across its quad, added onto the flare target. Alpha stays 0, so the
// premultiplied blend adds the colour.

layout(location = 0) in vec2 local;
layout(location = 1) flat in vec3 color;
layout(location = 2) flat in vec4 shape;

layout(location = 0) out vec4 outColor;

// LensFlareShape in LensFlare.cs.
const int GHOST = 0;
const int RING = 1;
const int POLYGON = 2;
const int STREAK = 3;
const int HALO = 4;

const float TAU = 6.28318531;

// A soft disc, brighter towards its rim, as a ghost of the aperture is.
float disc(float d) {
    return smoothstep(1.0, 0.75, d) * (0.45 + 0.55 * smoothstep(0.3, 0.92, d));
}

// Red reaches a little further than blue, as through glass that bends each colour its own way.
vec3 ghost(float d) {
    return vec3(disc(d * 0.97), disc(d), disc(d * 1.03));
}

float ring(float d) {
    float x = (d - 0.85) / 0.07;
    return exp(-x * x);
}

// A soft polygon of the given sides: the distance out is measured to the nearest side, not the nearest corner.
float polygon(vec2 p, float sides, float turn) {
    float sector = TAU / sides;
    float angle = mod(atan(p.y, p.x) + turn, sector) - 0.5 * sector;
    float edge = length(p) * cos(angle) / cos(0.5 * sector);
    return smoothstep(1.0, 0.8, edge) * (0.6 + 0.4 * smoothstep(0.4, 0.95, edge));
}

// Brightest through the light and fading out to either end; the quad is thin, and the line thinner inside it.
float streak(vec2 p) {
    float along = 1.0 - abs(p.x);
    float across = 2.5 * p.y;
    return along * along * along * exp(-across * across);
}

// A thin wide ring whose colours sit a little apart, red outermost, as a rainbow.
vec3 halo(float d) {
    vec3 x = (vec3(d) - vec3(0.92, 0.89, 0.86)) / 0.045;
    return exp(-x * x);
}

void main() {
    float d = length(local);
    int kind = int(shape.x + 0.5);
    vec3 amount;
    if (kind == GHOST) {
        amount = ghost(d);
    } else if (kind == RING) {
        amount = vec3(ring(d));
    } else if (kind == POLYGON) {
        amount = vec3(polygon(local, shape.y, shape.z));
    } else if (kind == STREAK) {
        amount = vec3(streak(local));
    } else {
        amount = halo(d);
    }

    outColor = vec4(color * amount, 0.0);
}
