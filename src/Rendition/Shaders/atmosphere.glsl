// The air round a planet like Earth, after Hillaire, "A Scalable and Production Ready Sky and
// Atmosphere Rendering Technique", 2020: a transmittance table, a table of the light scattered
// many times, and a sky-view table of what is seen from the ground. Distances are kilometres and
// coefficients per kilometre. The constants and the turbidity's aerosol must match
// Passes/Atmosphere.cs, which the tests check against hand-worked values.

const float ATMOSPHERE_PI = 3.14159265359;
const float PLANET_RADIUS = 6360.0;
const float ATMOSPHERE_RADIUS = 6460.0;
const float RAYLEIGH_HEIGHT = 8.0;
const float MIE_HEIGHT = 1.2;
const float MIE_ASYMMETRY = 0.8;
const vec3 RAYLEIGH_SCATTERING = vec3(5.802e-3, 13.558e-3, 33.1e-3);
const vec3 OZONE_ABSORPTION = vec3(0.650e-3, 1.881e-3, 0.085e-3);
const float OZONE_CENTRE = 25.0;
const float OZONE_HALF_WIDTH = 15.0;
const vec3 GROUND_ALBEDO = vec3(0.3);

// Where the sky is seen from: just above the ground, the same for every scene.
const float VIEW_RADIUS = PLANET_RADIUS + 0.05;

// The sun's light at the top of the air, in the renderer's units, where a sun light of about 8 is
// the real 100,000 lux. 13 gives a turbidity-3 sky with the sun 60 degrees up the average
// brightness the Preetham sky it replaced had, so scenes lit by the old sky keep their exposure.
const float SOLAR_STRENGTH = 13.0;

// The faint glow of a night sky, airglow and starlight scattered by the air, about eight stops
// below noon. The sky never goes darker than this, so night keeps a colour.
const vec3 NIGHT_GLOW = vec3(0.0010, 0.0016, 0.0030);

const ivec2 TRANSMITTANCE_SIZE = ivec2(256, 64);
const ivec2 MULTISCATTER_SIZE = ivec2(32, 32);
const ivec2 SKY_VIEW_SIZE = ivec2(192, 108);

// The air's aerosol, from the turbidity: per kilometre at the ground.
struct Aerosol {
    float scattering;
    float extinction;
};

struct Medium {
    vec3 scattering;
    vec3 extinction;
    vec3 rayleigh;
    float mie;
};

Medium mediumAt(float radius, Aerosol aerosol) {
    float height = max(radius - PLANET_RADIUS, 0.0);
    float rayleighDensity = exp(-height / RAYLEIGH_HEIGHT);
    float mieDensity = exp(-height / MIE_HEIGHT);
    float ozoneDensity = max(0.0, 1.0 - abs(height - OZONE_CENTRE) / OZONE_HALF_WIDTH);

    Medium medium;
    medium.rayleigh = RAYLEIGH_SCATTERING * rayleighDensity;
    medium.mie = aerosol.scattering * mieDensity;
    medium.scattering = medium.rayleigh + vec3(medium.mie);
    medium.extinction = medium.rayleigh + vec3(aerosol.extinction * mieDensity) + OZONE_ABSORPTION * ozoneDensity;
    return medium;
}

// The nearest distance along a ray from radius r, at cosine mu from straight up, to a sphere of
// radius R, or -1 if it misses. The ray starts inside or on it.
float distanceToSphere(float r, float mu, float sphere) {
    float discriminant = r * r * (mu * mu - 1.0) + sphere * sphere;
    if (discriminant < 0.0) {
        return -1.0;
    }

    float root = sqrt(discriminant);
    float near = -r * mu - root;
    return near >= 0.0 ? near : -r * mu + root;
}

bool hitsGround(float r, float mu) {
    return mu < 0.0 && r * r * (mu * mu - 1.0) + PLANET_RADIUS * PLANET_RADIUS >= 0.0;
}

// Bruneton's mapping of the transmittance table: x from the distance to the top of the air,
// y from the height. Only rays that miss the ground are kept.
vec2 transmittanceUv(float r, float mu) {
    float horizon = sqrt(ATMOSPHERE_RADIUS * ATMOSPHERE_RADIUS - PLANET_RADIUS * PLANET_RADIUS);
    float rho = sqrt(max(r * r - PLANET_RADIUS * PLANET_RADIUS, 0.0));
    float discriminant = r * r * (mu * mu - 1.0) + ATMOSPHERE_RADIUS * ATMOSPHERE_RADIUS;
    float d = max(0.0, -r * mu + sqrt(max(discriminant, 0.0)));
    float dMin = ATMOSPHERE_RADIUS - r;
    float dMax = rho + horizon;
    return vec2((d - dMin) / max(dMax - dMin, 1e-6), rho / horizon);
}

void transmittanceRadiusAndCosine(vec2 uv, out float r, out float mu) {
    float horizon = sqrt(ATMOSPHERE_RADIUS * ATMOSPHERE_RADIUS - PLANET_RADIUS * PLANET_RADIUS);
    float rho = horizon * uv.y;
    r = sqrt(rho * rho + PLANET_RADIUS * PLANET_RADIUS);
    float dMin = ATMOSPHERE_RADIUS - r;
    float dMax = rho + horizon;
    float d = dMin + uv.x * (dMax - dMin);
    mu = d == 0.0 ? 1.0 : (horizon * horizon - rho * rho - d * d) / (2.0 * r * d);
    mu = clamp(mu, -1.0, 1.0);
}

// The share of the light that crosses the air from radius r towards mu, 0 where the planet is
// in the way.
vec3 transmittanceTo(sampler2D table, float r, float mu) {
    if (hitsGround(r, mu)) {
        return vec3(0.0);
    }

    return textureLod(table, transmittanceUv(r, mu), 0.0).rgb;
}

// The light scattered many times, per unit of sun, at radius r with the sun at mu from straight up.
vec3 multipleScattering(sampler2D table, float r, float mu) {
    vec2 uv = vec2(mu * 0.5 + 0.5, (r - PLANET_RADIUS) / (ATMOSPHERE_RADIUS - PLANET_RADIUS));
    vec2 size = vec2(MULTISCATTER_SIZE);
    uv = (uv * (size - 1.0) + 0.5) / size;
    return textureLod(table, uv, 0.0).rgb;
}

float rayleighPhase(float cosine) {
    return 3.0 / (16.0 * ATMOSPHERE_PI) * (1.0 + cosine * cosine);
}

// Cornette and Shanks' phase, as Hillaire uses.
float miePhase(float cosine) {
    float g = MIE_ASYMMETRY;
    float k = 3.0 / (8.0 * ATMOSPHERE_PI) * (1.0 - g * g) / (2.0 + g * g);
    return k * (1.0 + cosine * cosine) / pow(1.0 + g * g - 2.0 * g * cosine, 1.5);
}

// The sky-view table: x the angle round from the sun, squared so it is finer towards the sun;
// y the angle from straight up, finer near the horizon, which sits at y = 0.5.
vec2 skyViewUv(float r, float viewCosine, float sunAcrossCosine) {
    float horizon = sqrt(max(r * r - PLANET_RADIUS * PLANET_RADIUS, 0.0));
    float beta = acos(clamp(horizon / r, -1.0, 1.0));
    float zenithToHorizon = ATMOSPHERE_PI - beta;
    float viewAngle = acos(clamp(viewCosine, -1.0, 1.0));

    float v;
    if (viewAngle < zenithToHorizon) {
        float coord = 1.0 - sqrt(max(1.0 - viewAngle / zenithToHorizon, 0.0));
        v = 0.5 * coord;
    } else {
        float coord = sqrt(max((viewAngle - zenithToHorizon) / beta, 0.0));
        v = 0.5 + 0.5 * coord;
    }

    float u = sqrt(clamp(-sunAcrossCosine * 0.5 + 0.5, 0.0, 1.0));
    vec2 size = vec2(SKY_VIEW_SIZE);
    return (vec2(u, v) * (size - 1.0) + 0.5) / size;
}

// The physical sky at or above the horizon, in the renderer's units, from the sky-view table.
vec3 physicalSky(sampler2D skyView, vec3 direction, vec3 sunDirection) {
    vec2 sunAcross = sunDirection.xz;
    vec2 viewAcross = direction.xz;
    float lengths = length(sunAcross) * length(viewAcross);
    float across = lengths > 1e-6 ? dot(sunAcross, viewAcross) / lengths : 1.0;
    return textureLod(skyView, skyViewUv(VIEW_RADIUS, direction.y, across), 0.0).rgb + NIGHT_GLOW;
}

void skyViewAngles(vec2 texel, float r, out float viewCosine, out float sunAcrossCosine) {
    vec2 uv = texel / vec2(SKY_VIEW_SIZE - 1);
    float horizon = sqrt(max(r * r - PLANET_RADIUS * PLANET_RADIUS, 0.0));
    float beta = acos(clamp(horizon / r, -1.0, 1.0));
    float zenithToHorizon = ATMOSPHERE_PI - beta;

    float viewAngle;
    if (uv.y < 0.5) {
        float coord = 1.0 - 2.0 * uv.y;
        viewAngle = zenithToHorizon * (1.0 - coord * coord);
    } else {
        float coord = uv.y * 2.0 - 1.0;
        viewAngle = zenithToHorizon + beta * coord * coord;
    }

    viewCosine = cos(viewAngle);
    sunAcrossCosine = -(uv.x * uv.x * 2.0 - 1.0);
}
