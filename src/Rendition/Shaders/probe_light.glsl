// The light a surface sends towards a light probe, for probe updates: the suns, every point and
// spot light in range, the bounce the probes themselves already hold, and its own glow. The sky
// reaches a probe through the directions where it sees nothing, not through here.
//
// Needs common.glsl, sampling.glsl, brdf.glsl, light_data.glsl and probes.glsl first, and two
// macros from the includer, each giving 0 for blocked to 1 for lit:
//   PROBE_SUN_VISIBILITY(p, n)    the shadow-casting sun's, at point p with normal n;
//   PROBE_LIGHT_VISIBILITY(i, p, n) local light i's, from its own shadow tile or 1 without one.
// See "Light probes" in docs/design.md.
#ifndef PROBE_LIGHT_GLSL
#define PROBE_LIGHT_GLSL

vec3 probeRadiance(vec3 position, vec3 normal, vec3 albedo, vec3 emissive) {
    // Light arriving straight from the lights, as irradiance; the diffuse surface sends albedo / pi of it on.
    vec3 direct = vec3(0.0);
    for (int i = 0; i < frame.lightCount; i++) {
        vec3 l = frame.lights[i].direction.xyz;
        float nDotL = dot(normal, l);
        if (nDotL <= 0.0) {
            continue;
        }

        float visibility = frame.lights[i].direction.w > 0.5 ? PROBE_SUN_VISIBILITY(position, normal) : 1.0;
        direct += frame.lights[i].radiance.rgb * nDotL * visibility;
    }

    // Every point and spot light of the frame, not only those in view: a probe behind the camera sees them too.
    for (int i = 0; i < frame.probeLightCount; i++) {
        LocalLight light = localLights[i];
        vec3 l;
        float reaching = localLightReaching(light, position, l);
        float nDotL = dot(normal, l);
        if (reaching <= 0.0 || nDotL <= 0.0) {
            continue;
        }

        direct += light.radianceShadow.rgb * reaching * nDotL * PROBE_LIGHT_VISIBILITY(i, position, normal);
    }

    // The probes' own light, as the lighting pass gives it, is what makes the second and later bounces.
    // Where no probe can tell yet, there is none.
    vec3 bounce = vec3(0.0);
    if (frame.lightProbeCounts.w > 0.5 || frame.probeCascadeCounts.w > 0.5) {
        bounce = max(probeIrradiance(position, normal, normal), vec3(0.0));
    }

    return albedo * (direct / PI + bounce) + emissive;
}

#endif
