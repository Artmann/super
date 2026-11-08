// What reflection probes saw, for the lit surfaces in lighting.frag and for glass in
// transparent.frag. Needs common.glsl first. See "Reflection probes" in docs/design.md.
#ifndef REFLECTION_PROBES_GLSL
#define REFLECTION_PROBES_GLSL

// Looks up what the room's probes saw in a direction, or the studio where there is no
// probe. The ray runs on to the box, so the lookup matches where it really lands.
vec3 probeReflection(samplerCubeArray probes, samplerCube studio, vec3 position, vec3 direction, float lod) {
    vec3 sum = vec3(0.0);
    float covered = 0.0;
    for (int i = 0; i < frame.probeCount && covered < 1.0; i++) {
        vec3 boxMin = frame.probes[i].boxMin.xyz;
        vec3 boxMax = frame.probes[i].boxMax.xyz;
        vec3 margins = min(position - boxMin, boxMax - position);
        float margin = min(margins.x, min(margins.y, margins.z));
        if (margin <= 0.0) {
            continue;
        }

        vec3 exits = max((boxMax - position) / direction, (boxMin - position) / direction);
        vec3 hit = position + direction * min(exits.x, min(exits.y, exits.z));
        vec3 local = hit - frame.probes[i].position.xyz;

        // Faces are stored the way a camera sees them, which mirrors the cube along z.
        vec3 seen = textureLod(probes, vec4(local.x, local.y, -local.z, float(i)), lod).rgb;

        float weight = clamp(margin / max(frame.probes[i].position.w, 1e-3), 0.0, 1.0) * (1.0 - covered);
        sum += seen * weight;
        covered += weight;
    }

    // Probes hold the scene at full strength, but the caller scales for the studio.
    vec3 scene = sum / max(frame.backgroundRadiance.a, 1e-3);
    return scene + textureLod(studio, direction, lod).rgb * (1.0 - covered);
}

#endif
