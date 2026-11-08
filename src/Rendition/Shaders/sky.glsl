// A clear sky from the sun's height and the air's turbidity: Preetham, Shirley and Smits,
// "A Practical Analytic Model for Daylight", 1999. The coefficients and zenith values must
// match Passes/SkyModel.cs, which the tests check against the paper.

// The model gives thousands of candela per square metre. This brings it to the renderer's
// units, where a sun of about 8 is the real 100,000 lux, so a clear sky lights a scene next
// to such a sun about as the real one does.
const float SKY_UNITS_PER_KILOCANDELA = 0.08;

// Everything that is the same for every direction, worked out once.
struct SkyState {
    vec3 a, b, c, d, e;  // Perez coefficients for luminance Y and chromaticities x, y
    vec3 zenithOverPerez; // the zenith's Yxy over the distribution straight up
    vec3 sun;            // towards the sun, held at the horizon when it has set
};

vec3 skyPerezShape(SkyState s, float cosTheta, float gamma, float cosGamma) {
    return (1.0 + s.a * exp(s.b / max(cosTheta, 1e-3))) * (1.0 + s.c * exp(s.d * gamma) + s.e * cosGamma * cosGamma);
}

SkyState skyState(vec3 sunDirection, float turbidity) {
    float t = turbidity;
    SkyState s;
    s.a = vec3(0.1787 * t - 1.4630, -0.0193 * t - 0.2592, -0.0167 * t - 0.2608);
    s.b = vec3(-0.3554 * t + 0.4275, -0.0665 * t + 0.0008, -0.0950 * t + 0.0092);
    s.c = vec3(-0.0227 * t + 5.3251, -0.0004 * t + 0.2125, -0.0079 * t + 0.2102);
    s.d = vec3(0.1206 * t - 2.5771, -0.0641 * t - 0.8989, -0.0441 * t - 1.6537);
    s.e = vec3(-0.0670 * t + 0.3703, -0.0033 * t + 0.0452, -0.0109 * t + 0.0529);

    // The model holds only while the sun is up. A sun straight below has no way across to
    // hold to, so it takes one.
    vec3 held = vec3(sunDirection.x, max(sunDirection.y, 0.0), sunDirection.z);
    s.sun = dot(held, held) > 1e-8 ? normalize(held) : vec3(1.0, 0.0, 0.0);
    float thetaS = acos(clamp(s.sun.y, 0.0, 1.0));

    float chi = (4.0 / 9.0 - t / 120.0) * (3.14159265359 - 2.0 * thetaS);
    float zenithY = (4.0453 * t - 4.9710) * tan(chi) - 0.2155 * t + 2.4192;
    vec4 angles = vec4(thetaS * thetaS * thetaS, thetaS * thetaS, thetaS, 1.0);
    float zenithX = t * t * dot(vec4(0.00166, -0.00375, 0.00209, 0.0), angles)
        + t * dot(vec4(-0.02903, 0.06377, -0.03202, 0.00394), angles)
        + dot(vec4(0.11693, -0.21196, 0.06052, 0.25886), angles);
    float zenithYChroma = t * t * dot(vec4(0.00275, -0.00610, 0.00317, 0.0), angles)
        + t * dot(vec4(-0.04214, 0.08970, -0.04153, 0.00516), angles)
        + dot(vec4(0.15346, -0.26756, 0.06670, 0.26688), angles);

    vec3 straightUp = skyPerezShape(s, 1.0, thetaS, s.sun.y);
    s.zenithOverPerez = vec3(zenithY, zenithX, zenithYChroma) / straightUp;
    return s;
}

// Linear red, green and blue in the renderer's units, for a direction at or above the horizon.
vec3 skyRadiance(SkyState s, vec3 direction) {
    float cosGamma = clamp(dot(direction, s.sun), -1.0, 1.0);
    vec3 yxy = s.zenithOverPerez * skyPerezShape(s, direction.y, acos(cosGamma), cosGamma);

    float luminance = max(yxy.x, 0.0) * SKY_UNITS_PER_KILOCANDELA;
    vec3 xyz = vec3(yxy.y / yxy.z, 1.0, (1.0 - yxy.y - yxy.z) / yxy.z) * luminance;
    vec3 rgb = mat3(
        3.2406, -0.9689, 0.0557,
        -1.5372, 1.8758, -0.2040,
        -0.4986, 0.0415, 1.0570) * xyz;
    return max(rgb, vec3(0.0));
}

vec3 preethamRadiance(vec3 direction, vec3 sunDirection, float turbidity) {
    return skyRadiance(skyState(sunDirection, turbidity), direction);
}

// What a hazy surface or puff melts into: the environment's sharpest level, but above the
// horizon of a clear sky the sky itself, as the sun's disc there would show through as a hot
// spot. skySun is the frame's: xyz towards the sun, w the turbidity, 0 for the bands.
vec3 skyBehind(samplerCube cube, vec3 direction, vec4 skySun) {
    if (skySun.w > 0.0 && direction.y >= 0.0) {
        return preethamRadiance(direction, skySun.xyz, skySun.w);
    }

    return textureLod(cube, direction, 0.0).rgb;
}

// The sun's disc, with an edge a little soft so it doesn't shimmer. disc.rgb is its brightness,
// disc.a the cosine of its angular radius.
vec3 sunDiscRadiance(vec3 direction, vec3 sunDirection, vec4 disc) {
    float edge = (1.0 - disc.a) * 0.15;
    return disc.rgb * smoothstep(disc.a - edge, disc.a + edge, dot(direction, sunDirection));
}
