#version 460
#extension GL_GOOGLE_include_directive : require

// The probe debug view (--debug-view probes): a small sphere round every light probe of the finest
// cascade and of the fixed grid, made from nothing but the vertex and instance numbers. The first
// instances are the cascade's probes, one per cell of its box, and the rest the fixed grid's. A cascade
// probe inside the fixed grid's box isn't drawn, as the grid's probes light everything there.
// See Passes/ProbeDebugPass.cs and "Light probes" in docs/design.md.

#include "common.glsl"

const float PI = 3.14159265359;

// Must match ProbeDebugPass.cs.
const int SEGMENTS = 12; // Round the sphere.
const int RINGS = 8;     // From pole to pole.
const int FIXED_GRID_LAYER = 4;

// A sphere's radius, as a share of the spacing of its probes, so neighbours never touch.
const float RADIUS_PER_SPACING = 0.15;

layout(location = 0) out vec3 outNormal;
layout(location = 1) flat out int outProbe;
layout(location = 2) flat out int outLayer;

// The point of the unit sphere at a segment round it and a ring down from the top.
vec3 spherePoint(int segment, int ring) {
    float down = PI * float(ring) / float(RINGS);
    float around = 2.0 * PI * float(segment) / float(SEGMENTS);
    return vec3(sin(down) * cos(around), cos(down), sin(down) * sin(around));
}

void main() {
    // Two triangles a quad of the sphere, wound to face out.
    const ivec2 corners[6] = ivec2[](ivec2(0, 0), ivec2(1, 0), ivec2(1, 1), ivec2(0, 0), ivec2(1, 1), ivec2(0, 1));
    int quad = gl_VertexIndex / 6;
    ivec2 corner = corners[gl_VertexIndex % 6];
    vec3 unit = spherePoint(quad % SEGMENTS + corner.x, quad / SEGMENTS + corner.y);

    ivec3 counts = ivec3(frame.probeCascadeCounts.xyz + 0.5);
    int cascadeProbes = counts.x * counts.y * counts.z;
    vec3 center;
    float spacing;
    bool hidden = false;
    if (gl_InstanceIndex < cascadeProbes) {
        // A cell of the box, counted from the world origin, wraps round the box to find its slot, as in probes.glsl.
        int index = gl_InstanceIndex;
        ivec3 cell = ivec3(index % counts.x, (index / counts.x) % counts.y, index / (counts.x * counts.y));
        vec4 origin = frame.probeCascadeOrigin[0];
        ivec3 world = ivec3(floor(origin.xyz + 0.5)) + cell;
        ivec3 slot = world - counts * ivec3(floor(vec3(world) / vec3(counts)));
        spacing = origin.w;
        center = vec3(world) * spacing;
        outProbe = slot.x + counts.x * (slot.y + counts.y * slot.z);
        outLayer = 0;

        if (frame.lightProbeCounts.w > 0.5) {
            vec3 low = frame.lightProbeMin.xyz;
            vec3 high = low + (frame.lightProbeCounts.xyz - 1.0) * frame.lightProbeMin.w;
            hidden = all(greaterThanEqual(center, low - 1e-3)) && all(lessThanEqual(center, high + 1e-3));
        }
    } else {
        int index = gl_InstanceIndex - cascadeProbes;
        // A grid this frame doesn't read, such as one turned off since the count was taken, isn't drawn.
        hidden = frame.lightProbeCounts.w < 0.5;
        ivec3 grid = max(ivec3(frame.lightProbeCounts.xyz + 0.5), ivec3(1));
        ivec3 cell = ivec3(index % grid.x, (index / grid.x) % grid.y, index / (grid.x * grid.y));
        spacing = frame.lightProbeMin.w;
        center = frame.lightProbeMin.xyz + vec3(cell) * spacing;
        outProbe = index;
        outLayer = FIXED_GRID_LAYER;
    }

    outNormal = unit;
    // Behind the near plane, so the sphere is clipped away whole.
    gl_Position = hidden
        ? vec4(0.0, 0.0, -1.0, 1.0)
        : frame.viewProjection * vec4(center + unit * spacing * RADIUS_PER_SPACING, 1.0);
}
