#version 460
#extension GL_GOOGLE_include_directive : require

// Shades a probe's sphere in the probe debug view with what the probe holds: each point shows the
// light the probe's own irradiance tile gives a surface facing the way that point faces, in the lit
// image's units, as a white surface there would be lit by it. A probe not captured yet is a grey
// wireframe, and a probe inside a wall isn't drawn, as the lighting skips both. The atlases come
// from the lighting pass's set. See Passes/ProbeDebugPass.cs.

#include "common.glsl"
#include "sampling.glsl"
#include "probes.glsl"

const float PI = 3.14159265359;

// An uncaptured probe's wires: lines of longitude round it and of latitude down it.
const vec3 UNCAPTURED_COLOR = vec3(0.5);
const float WIRES_AROUND = 8.0;
const float WIRES_DOWN = 6.0;

layout(location = 0) in vec3 normal;
layout(location = 1) flat in int probe;
layout(location = 2) flat in int layer;

layout(location = 0) out vec4 outColor;

void main() {
    vec3 n = normalize(normal);
    if (probeCaptured(probe, layer) <= 0.0) {
        // A line a pixel wide, whatever the sphere's size on screen.
        vec2 lines = vec2(atan(n.z, n.x) / (2.0 * PI) * WIRES_AROUND, acos(clamp(n.y, -1.0, 1.0)) / PI * WIRES_DOWN);
        vec2 fromLine = abs(fract(lines + 0.5) - 0.5) / max(fwidth(lines), vec2(1e-5));
        if (min(fromLine.x, fromLine.y) > 0.75) {
            discard;
        }

        outColor = vec4(UNCAPTURED_COLOR, 1.0);
        return;
    }

    if (probeInside(probe, layer)) {
        discard;
    }

    ivec2 atlasSize = textureSize(probeIrradianceAtlas, 0).xy;
    vec2 uv = probeTileUv(probe, n, PROBE_IRRADIANCE_TEXELS, atlasSize);
    outColor = vec4(texture(probeIrradianceAtlas, vec3(uv, float(layer))).rgb, 1.0);
}
