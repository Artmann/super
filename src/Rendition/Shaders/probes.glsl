// Soft light from the light probe volume: the light bounced off everything round a point,
// which the sky alone can't give. Needs common.glsl and sampling.glsl first.
// See "Light probes" in docs/design.md.
#ifndef PROBES_GLSL
#define PROBES_GLSL

layout(set = 1, binding = 11) uniform sampler2D probeIrradianceAtlas;
layout(set = 1, binding = 12) uniform sampler2D probeDepthAtlas;

// Must match ProbeVolume.cs.
const int PROBE_COLUMNS = 64;
const int PROBE_IRRADIANCE_TEXELS = 8;
const int PROBE_DEPTH_TEXELS = 16;

// Where a direction falls in a probe's tile, as a fraction of the atlas. The tile's border
// lets the filter blend across the octahedral map's edges.
vec2 probeTileUv(int probe, vec3 direction, int texels, vec2 atlasSize) {
    ivec2 tile = ivec2(probe % PROBE_COLUMNS, probe / PROBE_COLUMNS);
    vec2 inside = octahedralEncode(direction) * 0.5 + 0.5;
    vec2 pixel = vec2(tile * (texels + 2)) + 1.0 + inside * float(texels);
    return pixel / atlasSize;
}

// probe_fold.comp marks a probe inside a wall in its depth tile's first corner.
bool probeInside(int probe) {
    ivec2 tile = ivec2(probe % PROBE_COLUMNS, probe / PROBE_COLUMNS);
    return texelFetch(probeDepthAtlas, tile * (PROBE_DEPTH_TEXELS + 2), 0).g < 0.0;
}

// The light reaching a point from around n, blended from the eight probes round it, or -1
// where none of them can see it, so the caller falls back to the sky. The weights are the
// sums ProbeGrid.Weight in ProbeGrid.cs makes, and its tests pin them.
vec3 probeIrradiance(vec3 p, vec3 n, vec3 v) {
    vec3 first = frame.lightProbeMin.xyz;
    float spacing = frame.lightProbeMin.w;
    ivec3 counts = ivec3(frame.lightProbeCounts.xyz + 0.5);

    // Moving the point off the surface, and a little towards the viewer, keeps it from
    // reading the probe's own view of that surface, which would make it shadow itself.
    vec3 biased = p + spacing * 0.2 * (0.75 * n + 0.25 * v);
    ivec3 base = clamp(ivec3(floor((biased - first) / spacing)), ivec3(0), counts - 2);

    vec2 irradianceSize = vec2(textureSize(probeIrradianceAtlas, 0));
    vec2 depthSize = vec2(textureSize(probeDepthAtlas, 0));
    vec3 sum = vec3(0.0);
    float total = 0.0;
    for (int corner = 0; corner < 8; corner++) {
        ivec3 cell = base + ivec3(corner & 1, (corner >> 1) & 1, (corner >> 2) & 1);
        vec3 probePosition = first + vec3(cell) * spacing;

        vec3 offset = abs(biased - probePosition) / spacing;
        vec3 along = max(1.0 - offset, 0.0);
        float trilinear = along.x * along.y * along.z;
        if (trilinear <= 0.0) {
            continue;
        }

        int probe = cell.x + counts.x * (cell.y + counts.y * cell.z);
        if (probeInside(probe)) {
            continue;
        }

        // A probe behind the surface lights it least, though never quite not at all, so a
        // point with probes on one side only still gets some.
        vec3 towards = probePosition - p;
        float facing = 1.0;
        if (length(towards) > 1e-5) {
            float wrap = (dot(normalize(towards), n) + 1.0) * 0.5;
            facing = wrap * wrap;
        }

        // The probe saw something nearer than the point in its direction: the point is likely
        // behind it, so the probe counts for little.
        vec3 fromProbe = biased - probePosition;
        float distanceToProbe = length(fromProbe);
        float visible = 1.0;
        if (distanceToProbe > 1e-5) {
            vec2 moments = texture(probeDepthAtlas,
                probeTileUv(probe, fromProbe / distanceToProbe, PROBE_DEPTH_TEXELS, depthSize)).rg;
            if (distanceToProbe > moments.x) {
                float variance = abs(moments.y - moments.x * moments.x);
                float beyond = distanceToProbe - moments.x;
                float chebyshev = variance / (variance + beyond * beyond);
                visible = chebyshev * chebyshev * chebyshev;
            }
        }

        float weight = trilinear * facing * visible;
        vec3 irradiance = texture(probeIrradianceAtlas,
            probeTileUv(probe, n, PROBE_IRRADIANCE_TEXELS, irradianceSize)).rgb;
        sum += irradiance * weight;
        total += weight;
    }

    return total < 1e-3 ? vec3(-1.0) : sum / total;
}

#endif
