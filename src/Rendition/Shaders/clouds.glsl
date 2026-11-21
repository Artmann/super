// The clouds' density and the light they scatter, after Schneider's "The Real-time Volumetric
// Cloudscapes of Horizon Zero Dawn" (2015) and Nubis (2017), with Wrenninge's octaves of
// multiple scattering ("Oz: The Great and Volumetric", 2013) and Hillaire's energy-conserving
// step ("Physically Based Sky, Atmosphere and Cloud Rendering in Frostbite", 2016). Needs
// common.glsl, and the includer binds cloudShapeTexture, cloudDetailTexture and
// cloudWeatherTexture, all repeating. See "Clouds" in docs/design.md.

#include "cloud_noise.glsl"
#include "cloud_layer.glsl"
#include "cloud_light.glsl"

float cloudRemap(float value, float from, float to, float newFrom, float newTo) {
    return newFrom + (value - from) / (to - from) * (newTo - newFrom);
}

// The weather where a point lies over the ground: see cloudWeatherField.
vec4 cloudWeatherAt(vec3 relative) {
    vec2 uv = (relative.xz + frame.cloudWeather.xy) / CLOUD_WEATHER_PERIOD;
    return textureLod(cloudWeatherTexture, vec3(uv, 0.5), 0.0);
}

// How much of the sky the clouds cover over a point, the coverage moved up or down by the weather.
float cloudCoverageAt(vec4 weather) {
    return cloudCoverage(frame.cloudShape.x, weather);
}

// How the clouds fill out with height, from 0 at the base to 1 at the top: flat heaps hug the base, towers
// climb, and both round off at the bottom and taper towards the top.
float cloudProfile(float fraction, float kind) {
    float low = smoothstep(0.0, 0.06, fraction) * (1.0 - smoothstep(0.18, 0.4, fraction));
    float tall = smoothstep(0.0, 0.1, fraction) * (1.0 - smoothstep(0.45, 1.0, fraction));
    return mix(low, tall, kind);
}

// The cloud's density at a point this far from the camera, 0 to about 1, before frame.cloudShape.y scales it. The
// shape noise makes round heaps, which the coverage cuts down to what the sky holds; the detail noise then frays
// their edges, wispy near the base and billowing above, unless cheap or detail is 0, as far off, where it would only
// shimmer.
float cloudDensity(vec3 relative, float detail) {
    float height = cloudHeight(relative);
    float fraction = (height - frame.cloudShape.z) / frame.cloudShape.w;
    if (fraction <= 0.0 || fraction >= 1.0) {
        return 0.0;
    }

    vec4 weather = cloudWeatherAt(relative);
    float coverage = cloudCoverageAt(weather);
    float profile = cloudProfile(fraction, weather.g);
    if (coverage <= 0.0 || profile <= 0.0) {
        return 0.0;
    }

    // The tops lean along the wind, as the wind is stronger higher up.
    vec2 lean = frame.cloudLean.xy * fraction;
    vec3 p = vec3(relative.x + frame.cloudNoise.x + lean.x, height, relative.z + frame.cloudNoise.y + lean.y);
    vec4 shape = textureLod(cloudShapeTexture, p / CLOUD_SHAPE_PERIOD, 0.0);
    float cells = shape.g * 0.625 + shape.b * 0.25 + shape.a * 0.125;
    float carved = cloudRemap(shape.r, cells - 1.0, 1.0, 0.0, 1.0);
    // Carved twice, the noise sits from about 0.66 to 0.9; spread over 0 to 1, a coverage of c leaves about c of
    // the sky under cloud.
    float heaps = clamp(cloudRemap(carved, 0.66, 0.9, 0.0, 1.0), 0.0, 1.0) * profile;

    float cloud = clamp(cloudRemap(heaps, 1.0 - coverage, 1.0, 0.0, 1.0), 0.0, 1.0);
    if (cloud <= 0.0 || detail <= 0.0) {
        return cloud;
    }

    vec3 q = vec3(relative.x + frame.cloudNoise.z + lean.x, height, relative.z + frame.cloudNoise.w + lean.y);
    vec3 fine = textureLod(cloudDetailTexture, q / CLOUD_DETAIL_PERIOD, 0.0).rgb;
    float frays = fine.r * 0.625 + fine.g * 0.25 + fine.b * 0.125;
    float edge = mix(frays, 1.0 - frays, clamp(fraction * 6.0, 0.0, 1.0));
    return clamp(cloudRemap(cloud, edge * 0.45 * detail, 1.0, 0.0, 1.0), 0.0, 1.0);
}

// The sunlight a point in a cloud sends towards the camera, per unit of scattering, after the cloud between it and the
// light took depth of it: see cloudScattering. The powder darkens the edges facing the light a little, where light has
// had little cloud to scatter in from, and fades away looking towards the light, where the silver lining wins.
vec3 cloudSunlight(float depth, float cosine, float density) {
    float powder = 1.0 - exp(-density * 60.0 * CLOUD_EXTINCTION * 2.0);
    float facing = clamp(cosine * 0.5 + 0.5, 0.0, 1.0);
    return frame.cloudRadiance.rgb * cloudScattering(depth, cosine) * mix(powder, 1.0, facing * 0.7 + 0.3 * powder);
}

// How much of the light the cloud stops on its way up to a point, in optical depth, from a few steps towards the light,
// each longer than the last, the far ones without the detail.
float cloudDepthTowardsLight(vec3 relative, vec3 l) {
    float depth = 0.0;
    float step = frame.cloudShape.w * 0.02;
    float travelled = 0.0;
    for (int i = 0; i < 6; i++) {
        travelled += step * 0.5;
        depth += cloudDensity(relative + l * travelled, i < 3 ? 1.0 : 0.0) * step;
        travelled += step * 0.5;
        step *= 1.8;
    }

    return depth * frame.cloudShape.y * CLOUD_EXTINCTION;
}
