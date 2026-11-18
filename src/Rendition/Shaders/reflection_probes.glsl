// What reflection probes saw, for the lit surfaces in lighting.frag and for glass in
// transparent.frag. Needs common.glsl and light_data.glsl first. See "Reflections" in docs/design.md.
#ifndef REFLECTION_PROBES_GLSL
#define REFLECTION_PROBES_GLSL

// The most probes one pixel blends. The first ones its cluster lists, which are those
// ReflectionProbeOrder trusts most, usually cover it whole long before this.
const int BLENDED_PROBES = 4;

// Looks up what the room's probes saw in a direction, or the studio where there is no
// probe. Only the probes the pixel's cluster marks are tried, in the order they blend. The
// ray runs on to the box, so the lookup matches where it really lands.
vec3 probeReflection(samplerCubeArray probes, samplerCube studio, vec2 fragCoord, vec3 position, vec3 direction, float lod) {
    vec3 sum = vec3(0.0);
    float covered = 0.0;
    int blended = 0;
    uvec2 mask = clusterProbeMask(clusterAt(fragCoord, position));
    for (int word = 0; word < 2; word++) {
        uint bits = mask[word];
        while (bits != 0u && covered < 1.0 && blended < BLENDED_PROBES) {
            int i = word * 32 + findLSB(bits);
            bits &= bits - 1u;

            ReflectionProbe probe = reflectionProbes[i];
            vec3 boxMin = probe.boxMin.xyz;
            vec3 boxMax = probe.boxMax.xyz;
            vec3 margins = min(position - boxMin, boxMax - position);
            float margin = min(margins.x, min(margins.y, margins.z));
            if (margin <= 0.0) {
                continue;
            }

            vec3 exits = max((boxMax - position) / direction, (boxMin - position) / direction);
            vec3 hit = position + direction * min(exits.x, min(exits.y, exits.z));
            vec3 local = hit - probe.position.xyz;

            // Faces are stored the way a camera sees them, which mirrors the cube along z.
            vec3 seen = textureLod(probes, vec4(local.x, local.y, -local.z, probe.boxMin.w), lod).rgb;

            float weight = clamp(margin / max(probe.position.w, 1e-3), 0.0, 1.0) * (1.0 - covered);
            sum += seen * weight;
            covered += weight;
            blended++;
        }
    }

    // Probes hold the scene at full strength, but the caller scales for the studio.
    vec3 scene = sum / max(frame.backgroundRadiance.a, 1e-3);
    return scene + skyReflection(studio, direction, lod) * (1.0 - covered);
}

#endif
