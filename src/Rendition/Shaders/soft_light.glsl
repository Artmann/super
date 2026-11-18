// The soft light reaching a surface: the bake's probes inside a fresh baked space, the live light probes, and the sky
// where no probe can see. The lighting pass and reflection probe captures both light surfaces with it.
//
// Needs common.glsl, sampling.glsl and probes.glsl first, and two things from the includer: irradianceTexture, the
// environment's irradiance cube, and textures[], the material table's textures, which hold the drawn bake's probe
// atlases as they hold its lightmaps.
#ifndef SOFT_LIGHT_GLSL
#define SOFT_LIGHT_GLSL

// How much a point takes the baked space's probes over the live light, while the frame draws its fresh bake's probes: 1
// deep inside the box, falling to 0 at its faces over one probe spacing in from each, the product of each axis's
// smoothstep, and 0 outside the box or with no bake. A moving thing that crosses the box then blends from one light into
// the other with no jump. With the band off for a test (w 2), 1 anywhere in the box. Either way it is scaled by how far
// the bake has faded in (frame.bakedFade.x), so a bake that arrives on screen fades in instead of popping.
float bakedProbeWeight(vec3 position) {
    if (frame.bakedProbeCounts.w < 0.5) {
        return 0.0;
    }

    vec3 inside = min(position - frame.bakedSpaceMin.xyz, frame.bakedSpaceMax.xyz - position);
    if (any(lessThan(inside, vec3(0.0)))) {
        return 0.0;
    }

    if (frame.bakedProbeCounts.w > 1.5) {
        return frame.bakedFade.x;
    }

    vec3 band = smoothstep(vec3(0.0), vec3(frame.bakedProbeMin.w), inside);
    return band.x * band.y * band.z * frame.bakedFade.x;
}

// The soft light from the live frame: the live light probes, as probeIrradiance orders them, the view's fixed grid,
// then the cascades round the camera; then the sky, where none of the probes can see the point.
vec3 liveSoftLight(vec3 position, vec3 direction, vec3 v) {
    if (frame.lightProbeCounts.w > 0.5 || frame.probeCascadeCounts.w > 0.5) {
        vec3 fromProbes = probeIrradiance(position, direction, v);
        if (fromProbes.x >= 0.0) {
            return fromProbes;
        }
    }

    return skyIrradiance(irradianceTexture, direction) * frame.backgroundRadiance.a;
}

// The soft light reaching a point from around a direction, in the lit image's units. Inside a fresh baked space, the
// bake's probes, which hold its Baked lights the live frame leaves out, blended by bakedProbeWeight with the live soft
// light, which alone lights everywhere else; where none of the bake's probes sees the point, the live light alone.
// Lightmapped pixels take theirs from the lightmap instead, or in a chunk's edge band blend the two, so this lights
// dynamic objects and surfaces without a lightmap.
vec3 softLight(vec3 position, vec3 direction, vec3 v) {
    float weight = bakedProbeWeight(position);
    vec3 fromBake = vec3(0.0);
    if (weight > 0.0) {
        fromBake = bakedProbeIrradiance(textures[int(frame.bakedSpaceMin.w + 0.5)],
            textures[int(frame.bakedSpaceMax.w + 0.5)], frame.bakedProbeMin.xyz, frame.bakedProbeMin.w,
            ivec3(frame.bakedProbeCounts.xyz + 0.5), position, direction, v);
        if (fromBake.x < 0.0) {
            weight = 0.0;
        } else if (weight >= 1.0) {
            return fromBake;
        }
    }

    vec3 live = liveSoftLight(position, direction, v);
    return weight > 0.0 ? mix(live, fromBake, weight) : live;
}

#endif
