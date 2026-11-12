// The lights the baker's path tracer samples, and how a point is lit by one before shadows. A local light dims as
// localLightReaching in light_data.glsl has it at runtime, measured to the light's centre, and is sampled over its area:
// a point light over its sphere, seen as the disc facing the point, and a spot over its disc across its axis. A sun is
// sampled over its disc, a cone ShadowSettings.LightAngle across. See "The path tracer" in docs/design.md.

// Must match GpuBakeLight in Baking/Baker.cs.
struct BakeLight {
    vec4 positionRange;  // A local light's position and range; for a sun, the unit vector towards it.
    vec4 radianceRadius; // Radiance; w is a local light's radius, or for a sun the cosine of its disc's angular radius.
    vec4 directionCone;  // For a spot, the unit vector it shines along and the cosine of its outer angle; w -2 otherwise.
    vec4 fadeKind;       // x: 1 / (cos inner - cos outer); y: 1 for a sun, 0 otherwise; z: 1 for Mixed, 0 for Baked.
};

const float BAKE_TWO_PI = 6.28318531;

// How far a ray towards a sun reaches, in metres.
const float BAKE_SUN_DISTANCE = 100000.0;

// Two directions across a unit vector, from it alone (Duff et al. 2017).
void bakeAcross(vec3 n, out vec3 t, out vec3 b) {
    float s = n.z >= 0.0 ? 1.0 : -1.0;
    float a = -1.0 / (s + n.z);
    float c = n.x * n.y * a;
    t = vec3(1.0 + s * n.x * n.x * a, s * c, -s * n.x);
    b = vec3(c, s + n.y * n.y * a, -n.y);
}

bool bakeIsSun(BakeLight light) {
    return light.fadeKind.y > 0.5;
}

bool bakeIsMixed(BakeLight light) {
    return light.fadeKind.z > 0.5;
}

// Picks a point on the light, from two random numbers, as seen from position p. Returns the light it gives p
// unshadowed, as irradiance on a surface facing it: the light's radiance and its dimming with distance. l is the unit
// vector towards the point picked and distance how far it is. Zero when the light can't reach p.
vec3 bakeLightReaching(BakeLight light, vec3 p, vec2 u, out vec3 l, out float distance) {
    if (bakeIsSun(light)) {
        // Even over the cone's solid angle.
        float cosMax = light.radianceRadius.w;
        float cosTheta = 1.0 - u.x * (1.0 - cosMax);
        float sinTheta = sqrt(max(0.0, 1.0 - cosTheta * cosTheta));
        float phi = BAKE_TWO_PI * u.y;
        vec3 axis = light.positionRange.xyz;
        vec3 t, b;
        bakeAcross(axis, t, b);
        l = normalize(axis * cosTheta + (t * cos(phi) + b * sin(phi)) * sinTheta);
        distance = BAKE_SUN_DISTANCE;
        return light.radianceRadius.rgb;
    }

    vec3 toLight = light.positionRange.xyz - p;
    float d2 = dot(toLight, toLight);
    float range = light.positionRange.w;
    distance = 0.0;
    l = vec3(0.0, 1.0, 0.0);
    if (d2 >= range * range || d2 < 1e-8) {
        return vec3(0.0);
    }

    // The runtime's falloff, from localLightReaching, to the light's centre.
    float radius = light.radianceRadius.w;
    float r2 = radius * radius;
    vec3 centre = toLight * inversesqrt(d2);
    float edge = d2 / (range * range);
    float window = clamp(1.0 - edge * edge, 0.0, 1.0);
    float reaching = window * window * (1.0 + r2) / (d2 + r2);
    float cosOuter = light.directionCone.w;
    if (cosOuter > -1.5) {
        float s = clamp((dot(-centre, light.directionCone.xyz) - cosOuter) * light.fadeKind.x, 0.0, 1.0);
        reaching *= s * s;
    }

    if (reaching <= 0.0) {
        return vec3(0.0);
    }

    // A point even over the disc: across the spot's axis, or for a point light the sphere's outline facing p.
    vec3 axis = cosOuter > -1.5 ? light.directionCone.xyz : centre;
    vec3 t, b;
    bakeAcross(axis, t, b);
    float rho = radius * sqrt(u.x);
    float phi = BAKE_TWO_PI * u.y;
    vec3 onLight = light.positionRange.xyz + (t * cos(phi) + b * sin(phi)) * rho;
    vec3 towards = onLight - p;
    distance = length(towards);
    l = towards / max(distance, 1e-6);
    return light.radianceRadius.rgb * reaching;
}

// As bakeLightReaching, on a surface at p with normal n: times the cosine at p.
vec3 bakeSampleLight(BakeLight light, vec3 p, vec3 n, vec2 u, out vec3 l, out float distance) {
    return bakeLightReaching(light, p, u, l, distance) * max(dot(n, l), 0.0);
}
