// What the sky gives the shaders that draw on top of it: the sun's disc, what hazy things melt
// into, and the physical sky's air between the eye and a surface. The sky itself is drawn into
// the environment's cubes by environment.comp, from the tables in atmosphere.glsl.

// What a hazy surface or puff melts into: the sky behind it without the sun's disc, which would
// show through as a hot spot. background is the environment's disc-free cube.
vec3 skyBehind(samplerCube background, vec3 direction) {
    return textureLod(background, direction, 0.0).rgb;
}

// The sun's disc, with an edge a little soft so it doesn't shimmer. disc.rgb is its brightness,
// disc.a the cosine of its angular radius.
vec3 sunDiscRadiance(vec3 direction, vec3 sunDirection, vec4 disc) {
    float edge = (1.0 - disc.a) * 0.15;
    return disc.rgb * smoothstep(disc.a - edge, disc.a + edge, dot(direction, sunDirection));
}

// The moon's angular radius, twice the real moon's so it reads on screen. Must match SkyModel.MoonAngularRadius.
const float MOON_RADIUS = 0.009;

// The moon's disc where direction looks at it, and how much of the pixel it covers in a. It is a ball lit by the
// sun, so it is full opposite the sun and a crescent near it, with a little earthshine on its dark side and darker
// seas on its face. moon is towards it and radiance its lit face's brightness; pixel is the pixel's width in radians.
vec4 moonDisc(vec3 direction, vec3 moon, vec3 radiance, vec3 sunDirection, float pixel) {
    float facing = dot(direction, moon);
    if (facing <= 0.0) {
        return vec4(0.0);
    }

    vec3 right = abs(moon.y) < 0.99 ? normalize(cross(vec3(0.0, 1.0, 0.0), moon)) : vec3(1.0, 0.0, 0.0);
    vec3 up = cross(moon, right);
    vec2 across = vec2(dot(direction, right), dot(direction, up)) / facing / MOON_RADIUS;
    float distance = length(across);
    float edge = max(pixel / MOON_RADIUS, 0.02);
    float cover = 1.0 - smoothstep(1.0 - edge, 1.0 + edge, distance);
    if (cover <= 0.0) {
        return vec4(0.0);
    }

    vec2 point = across / max(distance, 1.0);
    vec3 normal = right * point.x + up * point.y - moon * sqrt(max(1.0 - dot(point, point), 0.0));
    float lit = max(dot(normal, sunDirection), 0.0) + 0.015;
    // A few dark seas, fixed to the disc, as the moon always shows us the same face.
    float seas = 0.0;
    seas += 1.0 - smoothstep(0.15, 0.45, length(point - vec2(-0.25, 0.3)));
    seas += 1.0 - smoothstep(0.1, 0.35, length(point - vec2(0.2, 0.15)));
    seas += 1.0 - smoothstep(0.1, 0.3, length(point - vec2(-0.05, -0.35)));
    float albedo = 1.0 - 0.3 * min(seas, 1.0);
    return vec4(radiance * lit * albedo, cover);
}

uint skyHash(uvec3 cell) {
    uint h = cell.x * 0x8da6b343u ^ cell.y * 0xd8163841u ^ cell.z * 0xcb1ab31fu;
    h ^= h >> 16;
    h *= 0x7feb352du;
    h ^= h >> 15;
    h *= 0x846ca68bu;
    return h ^ (h >> 16);
}

float skyRandom(inout uint state) {
    state = state * 747796405u + 2891336453u;
    uint word = ((state >> ((state >> 28u) + 4u)) ^ state) * 277803737u;
    return float((word >> 22u) ^ word) / 4294967295.0;
}

// Stars, fixed to the sky and turned rotation radians about the zenith. The sky is cut into cells, and a few hold a
// star at a random spot, most faint and a few bright, white to blue or orange. Each is spread over at least a pixel
// with its light kept the same, so it neither vanishes nor flickers as the view moves. brightness is the brightest
// star's peak; pixel is the pixel's width in radians. They thin out near the horizon, through more air.
vec3 starLight(vec3 direction, float brightness, float rotation, float pixel) {
    if (brightness <= 0.0 || direction.y <= 0.0) {
        return vec3(0.0);
    }

    float c = cos(rotation);
    float s = sin(rotation);
    vec3 turned = vec3(c * direction.x - s * direction.z, direction.y, s * direction.x + c * direction.z);
    const float CELLS = 120.0;
    vec3 scaled = turned * CELLS;
    uvec3 cell = uvec3(ivec3(floor(scaled)) + 4096);
    uint state = skyHash(cell);
    if (skyRandom(state) > 0.04) {
        return vec3(0.0);
    }

    // Kept off the cell's edges, so one cell holds all of its star's light.
    vec3 spot = floor(scaled) + 0.3 + 0.4 * vec3(skyRandom(state), skyRandom(state), skyRandom(state));
    float away = length(normalize(spot) - turned);
    float size = 0.0006;
    float spread = max(size, pixel * 0.7);
    float glow = exp(-(away * away) / (spread * spread)) * (size * size) / (spread * spread);
    // Most stars are faint: a cubed random gives few bright ones.
    float magnitude = skyRandom(state);
    magnitude = magnitude * magnitude * magnitude;
    float warmth = skyRandom(state);
    vec3 tint = warmth < 0.3 ? vec3(0.75, 0.85, 1.0) : warmth > 0.8 ? vec3(1.0, 0.8, 0.6) : vec3(1.0);
    float air = smoothstep(0.0, 0.2, direction.y);
    return tint * brightness * (0.05 + magnitude) * glow * air;
}

const float AIR_PI = 3.14159265359;

// The air between the eye and a surface distance metres away along direction, in closed form for
// air of the same density all the way: the surface fades by its transmittance and gains the light
// the air scatters towards the eye, from the sun and from the sky all round. extinction.rgb is per
// metre and extinction.a the aerosol's scattering per metre; sun.rgb is the sunlight reaching the
// air, a 1 when the fade is on; ambient is the sky's average radiance from every side, which the
// air scatters evenly. Passes/Atmosphere.Air packs extinction and sun; the Rayleigh coefficients
// here must match its RayleighScattering, in metres rather than kilometres.
vec3 throughAir(vec3 color, vec3 direction, float distance, vec4 extinction, vec4 sun, vec3 sunDirection, vec3 ambient) {
    if (sun.a < 0.5) {
        return color;
    }

    vec3 rayleigh = vec3(5.802e-6, 13.558e-6, 33.1e-6);
    float mie = extinction.a;
    float cosine = dot(direction, sunDirection);
    float rayleighPhase = 3.0 / (16.0 * AIR_PI) * (1.0 + cosine * cosine);
    float g = 0.8;
    float k = 3.0 / (8.0 * AIR_PI) * (1.0 - g * g) / (2.0 + g * g);
    float miePhase = k * (1.0 + cosine * cosine) / pow(1.0 + g * g - 2.0 * g * cosine, 1.5);

    vec3 source = sun.rgb * (rayleigh * rayleighPhase + mie * miePhase) + (rayleigh + mie) * ambient;
    vec3 through = exp(-extinction.rgb * distance);
    return color * through + source / max(extinction.rgb, vec3(1e-9)) * (1.0 - through);
}
