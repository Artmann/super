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
