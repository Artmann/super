// Height fog: air that thickens towards the ground, between the camera and what it sees.
// Needs common.glsl and sky.glsl. The sums must match Passes/Fog.cs, which the tests check.
// Near the camera, with the volumetric fog on, the volume in volumetric.glsl takes over.

#include "volumetric.glsl"

// How much light the fog takes per metre at a point: frame.fog.x at height frame.fog.y, and
// e times less every 1 / frame.fog.z metres up.
float fogDensity(vec3 position) {
    return min(frame.fog.x * exp(-frame.fog.z * (position.y - frame.fog.y)), 1000.0);
}

// The share of the light leaving position that reaches from, 1 with no fog. The density is an
// exponential in height, so its integral along the ray has a closed form.
float fogTransmittanceBetween(vec3 from, vec3 position) {
    float density = frame.fog.x;
    if (density <= 0.0) {
        return 1.0;
    }

    float falloff = frame.fog.z;
    // Taken from the ray's lower end, so a camera far above or below the fog's height never
    // meets inf - inf or 0 * inf.
    float low = min(from.y, position.y);
    float rise = falloff * abs(position.y - from.y);
    float along = rise > 1e-4 ? (1.0 - exp(-rise)) / rise : 1.0;
    float depth = density * exp(-falloff * (low - frame.fog.y)) * length(position - from) * along;
    return exp(-depth);
}

// The share of the light leaving position that reaches the camera.
float fogTransmittance(vec3 position) {
    return fogTransmittanceBetween(frame.cameraPosition.xyz, position);
}

// The sky that lights the fog seen along direction, without the sun's disc. Fog is lit by the
// sky all round it, not by the ground, so looking down it takes the sky's colour at the
// horizon that way.
vec3 fogSkyLight(samplerCube sky, vec3 direction) {
    vec3 color = frame.backgroundRadiance.rgb;
    if (frame.showEnvironment == 1) {
        vec3 level = vec3(direction.x, max(direction.y, 0.0), direction.z);
        level = dot(level, level) > 1e-8 ? normalize(level) : vec3(1.0, 0.0, 0.0);
        color = skyBehind(sky, level, frame.skySun) * frame.backgroundRadiance.a;
    }

    return color;
}

// What the fog adds where it hides things, looking along direction: the sky it is lit by and
// a glow towards each sun.
vec3 fogColor(samplerCube sky, vec3 direction) {
    vec3 color = fogSkyLight(sky, direction);
    vec3 glow = vec3(0.0);
    for (int i = 0; i < frame.lightCount; i++) {
        glow += frame.lights[i].radiance.rgb * pow(max(dot(direction, frame.lights[i].direction.xyz), 0.0), 8.0);
    }

    return color + frame.fog.w * glow;
}

// The light the fog adds in front of position, where kept is its fogTransmittance. What is
// seen there is then its own light times kept, plus this.
vec3 fogLight(vec3 position, float kept, samplerCube sky) {
    return fogColor(sky, normalize(position - frame.cameraPosition.xyz)) * (1.0 - kept);
}

#ifndef VOLUMETRIC_WRITER
// The fog between the camera and position: the light it adds in rgb, and the share of the
// light leaving position that gets through in a. Seen there is then its own light times a,
// plus rgb. With no volume it is the plain fog. Within the volume's reach the volume, lit by
// the sun's shadows and the point and spot lights, replaces it; past that the plain fog
// carries on from where the volume ends, seen through it.
vec4 fogInFront(vec3 position, samplerCube sky) {
    if (frame.volumetric.x <= 0.0) {
        float kept = fogTransmittance(position);
        return vec4(fogLight(position, kept, sky), kept);
    }

    vec2 uv = volumetricScreenUv(position);
    float depth = volumetricViewDepth(position);
    float reach = frame.volumetric.x;
    vec4 inside = volumetricLookup(uv, depth);
    if (depth <= reach) {
        return inside;
    }

    vec3 origin;
    vec3 along;
    volumetricRay(uv, origin, along);
    vec3 edge = origin + along * reach;
    float kept = fogTransmittanceBetween(edge, position);
    vec3 beyond = fogColor(sky, normalize(position - frame.cameraPosition.xyz)) * (1.0 - kept);
    return vec4(inside.rgb + inside.a * beyond, inside.a * kept);
}

// The sky seen through the volume on the ray through uv. The plain fog leaves the sky as it
// is, since it takes the sky's own colour, but the volume's shafts and glow show against it.
vec3 fogOverSky(vec3 background, vec2 uv) {
    if (frame.volumetric.x <= 0.0) {
        return background;
    }

    vec4 inside = volumetricLookup(uv, frame.volumetric.x);
    return background * inside.a + inside.rgb;
}
#endif
