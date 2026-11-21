// Being underwater: water swallows light with distance, red first, and the rippling
// surface focuses sunlight into moving nets of light (caustics) and hazy shafts.
// Needs common.glsl.

#include "water_absorption.glsl"
#include "volumetric.glsl"
#include "cloud_shadow.glsl"

const float NOTHING_DRAWN_DISTANCE = 1000.0;

// The shafts are a blurred, larger version of the pattern on the floor.
const float SHAFT_PATTERN_SCALE = 0.3;

// Bright thin lines that wander over time. Returns roughly 0 to 1.
float causticPattern(vec2 uv, float time) {
    const float TAU = 6.28318530718;
    const float SOFTNESS = 0.005;
    const int LAYERS = 4;

    vec2 p = mod(uv * TAU, TAU) - 250.0;
    vec2 wander = p;
    float lines = 1.0;
    for (int n = 0; n < LAYERS; n++) {
        float t = time * (1.0 - 3.5 / float(n + 1));
        wander = p + vec2(cos(t - wander.x) + sin(t + wander.y), sin(t - wander.y) + cos(t + wander.x));
        lines += 1.0 / length(vec2(p.x / (sin(wander.x + t) / SOFTNESS), p.y / (cos(wander.y + t) / SOFTNESS)));
    }

    lines = 1.17 - pow(lines / float(LAYERS), 1.4);
    return clamp(pow(abs(lines), 8.0), 0.0, 1.5);
}

// Follows the light up to the surface, because that's where the pattern is made.
float causticAt(vec3 position, vec3 l, float scale) {
    vec3 surface = position + l * metresToSurface(position, l);
    return causticPattern(surface.xz * scale, frame.time * 0.6);
}

// The sunlight left at a point after coming down through the water.
vec3 sunlightReaching(vec3 position, vec3 l, int light) {
    vec3 radiance = frame.lights[light].radiance.rgb * cloudShadowAt(position, light);
    if (!isUnderwater()) {
        return radiance;
    }

    float caustic = causticAt(position, l, frame.waterLight.y);
    return radiance * waterAbsorbed(metresToSurface(position, l)) * (1.0 + frame.waterLight.x * caustic);
}

// The colour of open water in a direction: dark below, bright towards the surface and sun.
vec3 waterColor(vec3 direction) {
    vec3 color = mix(frame.waterDeep.rgb, frame.waterShallow.rgb, smoothstep(-0.35, 0.75, direction.y));
    if (frame.lightCount > 0) {
        float glow = pow(max(dot(direction, frame.lights[0].direction.xyz), 0.0), 12.0);
        color += frame.waterShallow.rgb * glow * 1.5;
    }

    return color;
}

#ifndef VOLUMETRIC_WRITER
// What the camera sees of something, past all the water in front of it. Near the camera the
// volumetric fog holds the water's own light, the shafts and any fog volumes, summed through
// the water (see volumetric_inject.comp); past its reach, or with it off, the water's colour
// fills in, in closed form. Needs volumetric.glsl.
vec3 throughWater(vec3 color, vec3 position) {
    vec3 ray = position - frame.cameraPosition.xyz;
    float metres = length(ray);
    vec3 direction = ray / metres;
    vec3 kept = waterAbsorbed(metres);
    if (frame.volumetric.x <= 0.0) {
        return color * kept + waterColor(direction) * (1.0 - kept);
    }

    // The volume's light and fog in front of the point, then the water past its reach, which
    // starts as much dimmed as the volume leaves it.
    float depth = volumetricViewDepth(position);
    vec4 near = volumetricLookup(volumetricScreenUv(position), depth);
    vec3 keptAtReach = waterAbsorbed(metres * min(1.0, frame.volumetric.x / max(depth, 1e-6)));
    return near.rgb + near.a * (color * kept + waterColor(direction) * (keptAtReach - kept));
}
#endif
