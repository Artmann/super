// Soft light from the light probes: the light bounced off everything round a point, which the sky
// alone can't give. The probes live in two atlases with five layers each: the four cascades that
// follow the camera, finest first, and the fixed grid in layer 4. Needs common.glsl and
// sampling.glsl first. See "Light probes" in docs/design.md.
#ifndef PROBES_GLSL
#define PROBES_GLSL

// The lighting pass reads the atlases from its set 1. Probe updates bind them in a set of their own,
// and say which before including this.
#ifndef PROBE_ATLAS_SET
#define PROBE_ATLAS_SET 1
#endif

layout(set = PROBE_ATLAS_SET, binding = 11) uniform sampler2DArray probeIrradianceAtlas;
layout(set = PROBE_ATLAS_SET, binding = 12) uniform sampler2DArray probeDepthAtlas;

// Must match ProbeVolume.cs and probe_fold.glsl.
const int PROBE_IRRADIANCE_TEXELS = 8;
const int PROBE_DEPTH_TEXELS = 16;
const int PROBE_CASCADES = 4;
const int PROBE_FIXED_GRID_LAYER = 4;

// Below this much weight in all, a set of probes has nothing to say about a point.
const float PROBE_MIN_WEIGHT = 1e-3;

// The texel of a probe's tile's top-left border corner. Each atlas is as many tiles across as fit.
ivec2 probeTileCorner(int probe, int side, int atlasWidth) {
    int columns = atlasWidth / side;
    return ivec2(probe % columns, probe / columns) * side;
}

// Where a direction falls in a probe's tile, as a fraction of the atlas. The tile's border
// lets the filter blend across the octahedral map's edges.
vec2 probeTileUv(int probe, vec3 direction, int texels, ivec2 atlasSize) {
    vec2 inside = octahedralEncode(direction) * 0.5 + 0.5;
    vec2 pixel = vec2(probeTileCorner(probe, texels + 2, atlasSize.x)) + 1.0 + inside * float(texels);
    return pixel / vec2(atlasSize);
}

// The probe's valid flag: its irradiance alpha, 1 once it has been captured and 0 until then.
float probeCaptured(int probe, int layer) {
    int width = textureSize(probeIrradianceAtlas, 0).x;
    ivec2 corner = probeTileCorner(probe, PROBE_IRRADIANCE_TEXELS + 2, width);
    return texelFetch(probeIrradianceAtlas, ivec3(corner + 1, layer), 0).a;
}

// The capture marks a probe inside a wall in its depth tile's first corner.
bool probeInside(int probe, int layer) {
    int width = textureSize(probeDepthAtlas, 0).x;
    ivec2 corner = probeTileCorner(probe, PROBE_DEPTH_TEXELS + 2, width);
    return texelFetch(probeDepthAtlas, ivec3(corner, layer), 0).g < 0.0;
}

// The same for a bake's atlas, which has one layer.
bool probeInside(sampler2D depthAtlas, int probe) {
    int width = textureSize(depthAtlas, 0).x;
    return texelFetch(depthAtlas, probeTileCorner(probe, PROBE_DEPTH_TEXELS + 2, width), 0).g < 0.0;
}

// A probe behind the surface lights it least, though never quite not at all, so a point with
// probes on one side only still gets some.
float probeFacing(vec3 probePosition, vec3 p, vec3 n) {
    vec3 towards = probePosition - p;
    if (length(towards) <= 1e-5) {
        return 1.0;
    }

    float wrap = (dot(normalize(towards), n) + 1.0) * 0.5;
    return wrap * wrap;
}

// The probe saw something nearer than the point in its direction, at the mean and mean square
// distances in moments: the point is likely behind it, so the probe counts for little.
float probeVisible(vec2 moments, float distanceToProbe) {
    if (distanceToProbe <= moments.x) {
        return 1.0;
    }

    float variance = abs(moments.y - moments.x * moments.x);
    float beyond = distanceToProbe - moments.x;
    float chebyshev = variance / (variance + beyond * beyond);
    return chebyshev * chebyshev * chebyshev;
}

// The weighted light of the eight probes of one layer round a point, and the weights' total in w.
// The probes sit at first + cell * spacing for cells 0 to counts - 1. In the fixed grid probe
// x + counts.x * (y + counts.y * z) is in slot of the same number; a cascade's cell, counted from
// the world origin as origin + cell, wraps round the box to find its slot (ProbeCascades.Slot).
// The weights are the sums ProbeGrid.Weight in ProbeGrid.cs makes, and its tests pin them.
vec4 probeSum(vec3 p, vec3 n, vec3 v, vec3 first, float spacing, ivec3 counts, int layer, bool wraps, ivec3 origin) {
    // Moving the point off the surface, and a little towards the viewer, keeps it from
    // reading the probe's own view of that surface, which would make it shadow itself.
    vec3 biased = p + spacing * 0.2 * (0.75 * n + 0.25 * v);
    ivec3 base = clamp(ivec3(floor((biased - first) / spacing)), ivec3(0), counts - 2);

    ivec2 irradianceSize = textureSize(probeIrradianceAtlas, 0).xy;
    ivec2 depthSize = textureSize(probeDepthAtlas, 0).xy;
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

        ivec3 slotCell = cell;
        if (wraps) {
            // GLSL's % is undefined for negative numbers, so this wraps with floor.
            ivec3 world = origin + cell;
            slotCell = world - counts * ivec3(floor(vec3(world) / vec3(counts)));
        }

        int probe = slotCell.x + counts.x * (slotCell.y + counts.y * slotCell.z);

        // A probe not yet captured has nothing to give.
        trilinear *= probeCaptured(probe, layer);
        if (trilinear <= 0.0 || probeInside(probe, layer)) {
            continue;
        }

        vec3 fromProbe = biased - probePosition;
        float distanceToProbe = length(fromProbe);
        float visible = 1.0;
        if (distanceToProbe > 1e-5) {
            vec2 depthUv = probeTileUv(probe, fromProbe / distanceToProbe, PROBE_DEPTH_TEXELS, depthSize);
            visible = probeVisible(texture(probeDepthAtlas, vec3(depthUv, float(layer))).rg, distanceToProbe);
        }

        float weight = trilinear * probeFacing(probePosition, p, n) * visible;
        vec2 irradianceUv = probeTileUv(probe, n, PROBE_IRRADIANCE_TEXELS, irradianceSize);
        vec3 irradiance = texture(probeIrradianceAtlas, vec3(irradianceUv, float(layer))).rgb;
        sum += irradiance * weight;
        total += weight;
    }

    return vec4(sum, total);
}

// A bake's probes' light at a point, the same blend of the eight round it as probeSum, or -1 where
// none of them can see it. A bake lays its tiles out as the live atlases do, in one layer, from
// first, spacing metres apart, counts along each axis, and every probe in it was captured. Read at
// level 0 explicitly, since a bake's atlases sit in the material table, whose sampler filters
// anisotropically, and derivatives jump wherever neighbouring pixels read different tiles.
vec3 bakedProbeIrradiance(sampler2D irradianceAtlas, sampler2D depthAtlas, vec3 first, float spacing, ivec3 counts,
    vec3 p, vec3 n, vec3 v) {
    vec3 biased = p + spacing * 0.2 * (0.75 * n + 0.25 * v);
    ivec3 base = clamp(ivec3(floor((biased - first) / spacing)), ivec3(0), counts - 2);

    ivec2 irradianceSize = textureSize(irradianceAtlas, 0);
    ivec2 depthSize = textureSize(depthAtlas, 0);
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
        if (probeInside(depthAtlas, probe)) {
            continue;
        }

        vec3 fromProbe = biased - probePosition;
        float distanceToProbe = length(fromProbe);
        float visible = 1.0;
        if (distanceToProbe > 1e-5) {
            vec2 depthUv = probeTileUv(probe, fromProbe / distanceToProbe, PROBE_DEPTH_TEXELS, depthSize);
            visible = probeVisible(textureLod(depthAtlas, depthUv, 0.0).rg, distanceToProbe);
        }

        float weight = trilinear * probeFacing(probePosition, p, n) * visible;
        vec2 irradianceUv = probeTileUv(probe, n, PROBE_IRRADIANCE_TEXELS, irradianceSize);
        sum += textureLod(irradianceAtlas, irradianceUv, 0.0).rgb * weight;
        total += weight;
    }

    return total < PROBE_MIN_WEIGHT ? vec3(-1.0) : sum / total;
}

// How deep a point is inside a cascade, in cells from the edge of the part that holds it, or below
// 0 when it doesn't hold it. A cascade holds a point one cell in from its box on every side, so all
// eight probes round it are inside: the rule ProbeCascades.Finest uses.
float probeCascadeDepth(int cascade, vec3 p, ivec3 counts) {
    vec4 origin = frame.probeCascadeOrigin[cascade];
    float spacing = origin.w;
    vec3 low = (origin.xyz + 1.0) * spacing;
    vec3 high = (origin.xyz + vec3(counts) - 1.0) * spacing;
    if (any(lessThan(p, low)) || any(greaterThanEqual(p, high))) {
        return -1.0;
    }

    vec3 depth = min(p - low, high - p) / spacing;
    return min(depth.x, min(depth.y, depth.z));
}

vec4 probeCascadeSum(int cascade, vec3 p, vec3 n, vec3 v, ivec3 counts) {
    vec4 origin = frame.probeCascadeOrigin[cascade];
    ivec3 cell = ivec3(floor(origin.xyz + 0.5));
    return probeSum(p, n, v, origin.xyz * origin.w, origin.w, counts, cascade, true, cell);
}

// The light reaching a point from around n, or -1 where no probe can tell, so the caller falls
// back to the sky. In order: the fixed grid, wherever its probes reach; then the finest cascade
// that holds the point, fading into the next coarser one across its outermost cell; then, while a
// cascade's probes round the point aren't captured or can't see it, the next coarser one.
vec3 probeIrradiance(vec3 p, vec3 n, vec3 v) {
    if (frame.lightProbeCounts.w > 0.5) {
        ivec3 counts = ivec3(frame.lightProbeCounts.xyz + 0.5);
        vec4 grid = probeSum(p, n, v, frame.lightProbeMin.xyz, frame.lightProbeMin.w, counts,
            PROBE_FIXED_GRID_LAYER, false, ivec3(0));
        if (grid.w >= PROBE_MIN_WEIGHT) {
            return grid.rgb / grid.w;
        }
    }

    if (frame.probeCascadeCounts.w > 0.5) {
        ivec3 counts = ivec3(frame.probeCascadeCounts.xyz + 0.5);
        for (int c = 0; c < PROBE_CASCADES; c++) {
            float depth = probeCascadeDepth(c, p, counts);
            if (depth < 0.0) {
                continue;
            }

            vec4 fine = probeCascadeSum(c, p, n, v, counts);
            if (fine.w < PROBE_MIN_WEIGHT) {
                continue;
            }

            vec3 light = fine.rgb / fine.w;
            if (depth < 1.0 && c + 1 < PROBE_CASCADES && probeCascadeDepth(c + 1, p, counts) >= 0.0) {
                vec4 coarse = probeCascadeSum(c + 1, p, n, v, counts);
                if (coarse.w >= PROBE_MIN_WEIGHT) {
                    light = mix(coarse.rgb / coarse.w, light, depth);
                }
            }

            return light;
        }
    }

    return vec3(-1.0);
}

#endif
