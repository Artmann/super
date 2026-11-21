// How light scatters in the clouds, shared by the clouds' passes and the sky captures in
// environment.comp, so the light the captures give off matches the clouds on screen: Wrenninge's
// octaves of multiple scattering ("Oz: The Great and Volumetric", 2013) over a two-lobed
// Henyey-Greenstein phase. See "Clouds" in docs/design.md.
#ifndef CLOUD_LIGHT_GLSL
#define CLOUD_LIGHT_GLSL

// How much light a metre of the densest cloud stops, at a density of 1: real cumulus stops about this.
const float CLOUD_EXTINCTION = 0.04;

const float CLOUD_PI = 3.14159265359;

float cloudHenyeyGreenstein(float cosine, float g) {
    float g2 = g * g;
    return (1.0 - g2) / (4.0 * CLOUD_PI * pow(max(1.0 + g2 - 2.0 * g * cosine, 1e-4), 1.5));
}

// Light scattered once on its way through a cloud mostly goes on forwards, which gives the silver lining towards the
// sun; a little comes back, which lights the clouds' faces with the sun behind the camera.
float cloudPhase(float cosine, float spread) {
    return mix(cloudHenyeyGreenstein(cosine, 0.8 * spread), cloudHenyeyGreenstein(cosine, -0.3 * spread), 0.3);
}

// How much of the light reaching a cloud a point in it sends towards the camera, per unit of scattering, after the
// cloud between it and the light took depth of it (optical depth): five octaves of light scattered many times, each
// 0.7 times as strong, 0.35 times as dimmed and half as forward as the last. That is brighter than Wrenninge's bound
// allows, standing in for the many orders the octaves leave out, so thick clouds glow white in the sun as real ones do.
float cloudScattering(float depth, float cosine) {
    float light = 0.0;
    float strength = 1.0;
    float dimming = 1.0;
    float spread = 1.0;
    for (int octave = 0; octave < 5; octave++) {
        light += strength * cloudPhase(cosine, spread) * exp(-dimming * depth);
        strength *= 0.7;
        dimming *= 0.35;
        spread *= 0.5;
    }

    return light;
}

// The sky's light round a cloud at a fraction of the layer's height: the bottoms see the darker horizon and the
// ground, the tops the whole sky.
vec3 cloudSkyAround(float fraction, vec3 zenith, vec3 horizon) {
    return mix(horizon * 0.4, (zenith + horizon) * 0.5, clamp(fraction, 0.0, 1.0));
}

// How much of the sky the cirrus hides, from the weather's streaks, its amount, and how far off it lies, fading out
// towards the horizon.
float cloudCirrusCover(float streaks, float amount, float distance) {
    return clamp(streaks * amount * 1.4, 0.0, 0.8) * (1.0 - smoothstep(60000.0, 160000.0, distance));
}

// The light the cirrus sends on, from the light on it, mostly forwards, and the sky above.
vec3 cloudCirrusLight(vec3 radiance, float cosine, vec3 zenith) {
    return radiance * mix(cloudHenyeyGreenstein(cosine, 0.6), 1.0 / (4.0 * CLOUD_PI), 0.5) + zenith * 0.7;
}

#endif
