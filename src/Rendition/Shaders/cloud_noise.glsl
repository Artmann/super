// The noise the clouds are made of, all of it periodic so the textures tile: gradient noise and
// cellular noise in two and three dimensions, hashed with PCG3D (Jarzynski and Olano, "Hash
// Functions for GPU Rendering", JCGT 2020) rather than with sines, and the weather field that
// says where the clouds gather, which cloud_noise.comp bakes into the weather map that the march
// and the sky captures both read, so the captures put the clouds where the view does.
// See "Clouds" in docs/design.md.

// How many metres one tile of each texture covers across the ground. Must match CloudModel.
const float CLOUD_SHAPE_PERIOD = 6000.0;
const float CLOUD_DETAIL_PERIOD = 500.0;
const float CLOUD_WEATHER_PERIOD = 40960.0;

// The cirrus's height above the ground, in metres.
const float CIRRUS_ALTITUDE = 8000.0;

uvec3 pcg3d(uvec3 v) {
    v = v * 1664525u + 1013904223u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    v ^= v >> 16u;
    v.x += v.y * v.z;
    v.y += v.z * v.x;
    v.z += v.x * v.y;
    return v;
}

// A cell's three random numbers from 0 to 1, the same for every cell a period apart. seed keeps
// the layers of noise apart.
vec3 cellRandom(ivec3 cell, ivec3 period, uint seed) {
    ivec3 wrapped = ((cell % period) + period) % period;
    return vec3(pcg3d(uvec3(wrapped) + uvec3(seed * 7919u, seed * 104729u, seed * 1299709u))) * (1.0 / 4294967295.0);
}

vec3 quintic(vec3 t) {
    return t * t * t * (t * (t * 6.0 - 15.0) + 10.0);
}

// Gradient noise at p, in cells, repeating every period cells: from about -1 to 1.
float gradientNoise(vec3 p, ivec3 period, uint seed) {
    ivec3 cell = ivec3(floor(p));
    vec3 f = fract(p);
    vec3 u = quintic(f);
    float corners[8];
    for (int i = 0; i < 8; i++) {
        ivec3 corner = ivec3(i & 1, (i >> 1) & 1, (i >> 2) & 1);
        vec3 g = normalize(cellRandom(cell + corner, period, seed) * 2.0 - 1.0 + 1e-6);
        corners[i] = dot(g, f - vec3(corner));
    }

    float x0 = mix(mix(corners[0], corners[1], u.x), mix(corners[2], corners[3], u.x), u.y);
    float x1 = mix(mix(corners[4], corners[5], u.x), mix(corners[6], corners[7], u.x), u.y);
    return mix(x0, x1, u.z) * 1.6;
}

// Cellular noise at p, in cells, repeating every period cells: the distance to the nearest of one
// random point a cell, turned over so the cells are round bumps, 1 at their points.
float cellularNoise(vec3 p, ivec3 period, uint seed) {
    ivec3 cell = ivec3(floor(p));
    vec3 f = fract(p);
    float nearest = 1.0;
    for (int z = -1; z <= 1; z++) {
        for (int y = -1; y <= 1; y++) {
            for (int x = -1; x <= 1; x++) {
                ivec3 offset = ivec3(x, y, z);
                vec3 point = vec3(offset) + cellRandom(cell + offset, period, seed);
                vec3 d = point - f;
                nearest = min(nearest, dot(d, d));
            }
        }
    }

    return 1.0 - sqrt(nearest);
}

// Three octaves of cellular noise from frequency cells a tile, each twice as fine and half as strong.
float cellularOctaves(vec3 uvw, int frequency, uint seed) {
    float sum = 0.0;
    float weight = 0.625;
    int f = frequency;
    for (int octave = 0; octave < 3; octave++) {
        sum += cellularNoise(uvw * float(f), ivec3(f), seed + uint(octave)) * weight;
        weight = octave == 0 ? 0.25 : 0.125;
        f *= 2;
    }

    return sum;
}

// Octaves of gradient noise from frequency cells a tile, from 0 to 1.
float gradientOctaves(vec3 uvw, int frequency, int octaves, uint seed) {
    float sum = 0.0;
    float weight = 0.5;
    float total = 0.0;
    int f = frequency;
    for (int octave = 0; octave < octaves; octave++) {
        sum += gradientNoise(uvw * float(f), ivec3(f), seed + uint(octave)) * weight;
        total += weight;
        weight *= 0.5;
        f *= 2;
    }

    return clamp(sum / total * 0.5 + 0.5, 0.0, 1.0);
}

// The same in two dimensions: the third axis is one cell deep, so it doesn't change.
float gradientOctaves2(vec2 uv, ivec2 frequency, int octaves, uint seed) {
    float sum = 0.0;
    float weight = 0.5;
    float total = 0.0;
    ivec2 f = frequency;
    for (int octave = 0; octave < octaves; octave++) {
        sum += gradientNoise(vec3(uv * vec2(f), 0.5), ivec3(f, 1), seed + uint(octave)) * weight;
        total += weight;
        weight *= 0.5;
        f *= 2;
    }

    return clamp(sum / total * 0.5 + 0.5, 0.0, 1.0);
}

float cellularOctaves2(vec2 uv, int frequency, uint seed) {
    float sum = 0.0;
    float weight = 0.625;
    int f = frequency;
    for (int octave = 0; octave < 3; octave++) {
        float nearest = 1.0;
        vec2 p = uv * float(f);
        ivec2 cell = ivec2(floor(p));
        vec2 r = fract(p);
        for (int y = -1; y <= 1; y++) {
            for (int x = -1; x <= 1; x++) {
                ivec2 offset = ivec2(x, y);
                vec2 point = vec2(offset) + cellRandom(ivec3(cell + offset, 0), ivec3(f, f, 1), seed + uint(octave)).xy;
                vec2 d = point - r;
                nearest = min(nearest, dot(d, d));
            }
        }

        sum += (1.0 - sqrt(nearest)) * weight;
        weight = octave == 0 ? 0.25 : 0.125;
        f *= 2;
    }

    return sum;
}

// The weather at uv, a fraction of the weather tile: r how much more or less cloud than the
// coverage, 0.5 for neither, so the clouds gather in some places and thin out in others; g what
// kind, from flat heaps at 0 to tall towers at 1; b the cirrus's streaks; a small puffs, which
// the flat clouds of the lower settings and the sky captures break up with.
vec4 cloudWeatherField(vec2 uv) {
    float broad = gradientOctaves2(uv, ivec2(3), 4, 11u);
    float clumps = cellularOctaves2(uv, 12, 23u);
    float gather = clamp(broad * 0.65 + clumps * 0.55 - 0.15, 0.0, 1.0);

    float kind = smoothstep(0.3, 0.75, gradientOctaves2(uv, ivec2(2), 3, 37u));

    // Streaks along a slant: the noise is stretched five times across them, and still tiles.
    vec2 slant = vec2(uv.x + uv.y, uv.x - uv.y);
    float streaks = gradientOctaves2(slant, ivec2(4, 20), 4, 53u);
    float patches = gradientOctaves2(uv, ivec2(5), 3, 61u);
    float cirrus = smoothstep(0.45, 0.8, streaks) * smoothstep(0.3, 0.65, patches);

    float puffs = cellularOctaves2(uv, 64, 71u);
    return vec4(gather, kind, cirrus, puffs);
}

// How much of the sky the clouds cover under this weather: the settings' coverage moved up or down by its r.
float cloudCoverage(float coverage, vec4 weather) {
    return clamp(coverage + (weather.r - 0.5) * 0.7, 0.0, 1.0);
}
