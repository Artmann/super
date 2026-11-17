// Fog volumes: boxes, ellipsoids, cylinders and cones of fog a game places in the world, which
// volumetric_inject.comp adds to each cell of the volumetric fog. frame.volumetricGrid.z says how
// many there are this frame. The shapes and the edge fade must match Passes/FogVolumePack.cs,
// which the tests check; the noise is only here.
#ifndef FOG_VOLUMES_GLSL
#define FOG_VOLUMES_GLSL

// Must match GpuFogVolume in Passes/FogVolumePack.cs.
struct FogVolume {
    mat4 worldToLocal;      // From the world into the unit shape.
    vec4 colorDensity;      // xyz the colour, w the density per metre.
    vec4 shapeFadeNoise;    // x the shape (0 box, 1 ellipsoid, 2 cylinder, 3 cone), y the edge fade, z the noise, w 1 / its size in metres.
    vec4 windBounds;        // xyz the wind in metres a second, w the radius of a sphere round the centre that holds the shape.
    vec4 centre;            // xyz the shape's centre in the world.
};

layout(std430, set = 1, binding = 5) readonly buffer FogVolumeBuffer {
    FogVolume fogVolumes[];
};

// How far in from the surface of a unit shape a local point is: 0 on it, 1 at the middle,
// below 0 outside.
float fogVolumeInner(int shape, vec3 p) {
    float across = length(p.xz);
    float ends = 1.0 - 2.0 * abs(p.y);
    if (shape == 0) {
        return 1.0 - 2.0 * max(abs(p.x), max(abs(p.y), abs(p.z)));
    }

    if (shape == 1) {
        return 1.0 - 2.0 * length(p);
    }

    if (shape == 2) {
        return min(1.0 - 2.0 * across, ends);
    }

    // The cone's radius shrinks from 0.5 at its base, y -0.5, to nothing at its tip, y 0.5.
    return min(1.0 - across / max(0.5 * (0.5 - p.y), 1e-6), ends);
}

// A random gradient for a lattice point, each part -1 to 1.
vec3 fogVolumeGradient(vec3 cell) {
    vec3 p = fract(cell * vec3(0.1031, 0.1030, 0.0973));
    p += dot(p, p.yxz + 33.33);
    return fract((p.xxy + p.yxx) * p.zyx) * 2.0 - 1.0;
}

// Gradient noise, about -1 to 1, smooth with features one unit across.
float fogVolumeGradientNoise(vec3 p) {
    vec3 i = floor(p);
    vec3 f = p - i;
    vec3 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float n000 = dot(fogVolumeGradient(i), f);
    float n100 = dot(fogVolumeGradient(i + vec3(1, 0, 0)), f - vec3(1, 0, 0));
    float n010 = dot(fogVolumeGradient(i + vec3(0, 1, 0)), f - vec3(0, 1, 0));
    float n110 = dot(fogVolumeGradient(i + vec3(1, 1, 0)), f - vec3(1, 1, 0));
    float n001 = dot(fogVolumeGradient(i + vec3(0, 0, 1)), f - vec3(0, 0, 1));
    float n101 = dot(fogVolumeGradient(i + vec3(1, 0, 1)), f - vec3(1, 0, 1));
    float n011 = dot(fogVolumeGradient(i + vec3(0, 1, 1)), f - vec3(0, 1, 1));
    float n111 = dot(fogVolumeGradient(i + vec3(1, 1, 1)), f - vec3(1, 1, 1));
    return mix(
        mix(mix(n000, n100, u.x), mix(n010, n110, u.x), u.y),
        mix(mix(n001, n101, u.x), mix(n011, n111, u.x), u.y),
        u.z);
}

// Three octaves of gradient noise, 0 to 1, pushed apart so the fog has clear gaps and thick patches.
float fogVolumeNoise(vec3 p) {
    float n = 0.5 * fogVolumeGradientNoise(p)
        + 0.25 * fogVolumeGradientNoise(p * 2.03 + 17.0)
        + 0.125 * fogVolumeGradientNoise(p * 4.01 + 41.0);
    return smoothstep(0.25, 0.75, 0.5 + n / 0.875);
}

// A fog volume's density per metre at a point in the world: 0 outside the shape, rising over
// its edge fade to full, and broken up by noise that drifts with the wind.
float fogVolumeDensity(FogVolume volume, vec3 position, float time) {
    vec3 offset = position - volume.centre.xyz;
    float bounds = volume.windBounds.w;
    if (dot(offset, offset) > bounds * bounds) {
        return 0.0;
    }

    float inner = fogVolumeInner(int(volume.shapeFadeNoise.x + 0.5), (volume.worldToLocal * vec4(position, 1.0)).xyz);
    if (inner < 0.0) {
        return 0.0;
    }

    float fade = volume.shapeFadeNoise.y;
    float density = volume.colorDensity.w * (fade > 0.0 ? clamp(inner / fade, 0.0, 1.0) : 1.0);
    float noise = volume.shapeFadeNoise.z;
    if (noise > 0.0) {
        density *= mix(1.0, fogVolumeNoise((offset - volume.windBounds.xyz * time) * volume.shapeFadeNoise.w), noise);
    }

    return density;
}

#endif
