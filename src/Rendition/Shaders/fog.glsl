// Height fog: air that thickens towards the ground, between the camera and what it sees.
// Needs common.glsl and sky.glsl. The sums must match Passes/Fog.cs, which the tests check.

// The share of the light leaving position that reaches the camera, 1 with no fog. The density
// is frame.fog.x at height frame.fog.y and falls by e every 1 / frame.fog.z metres up, an
// exponential in height, so its integral along the ray has a closed form.
float fogTransmittance(vec3 position) {
    float density = frame.fog.x;
    if (density <= 0.0) {
        return 1.0;
    }

    vec3 from = frame.cameraPosition.xyz;
    float falloff = frame.fog.z;
    float climb = falloff * (position.y - from.y);
    float along = abs(climb) > 1e-4 ? (1.0 - exp(-climb)) / climb : 1.0;
    float depth = density * exp(-falloff * (from.y - frame.fog.y)) * length(position - from) * along;
    return exp(-depth);
}

// What the fog adds where it hides things, looking along direction: the sky it is lit by,
// without the sun's disc, and a glow towards each sun. Fog is lit by the sky all round it,
// not by the ground, so looking down it takes the sky's colour at the horizon that way.
vec3 fogColor(samplerCube sky, vec3 direction) {
    vec3 color = frame.backgroundRadiance.rgb;
    if (frame.showEnvironment == 1) {
        vec3 level = vec3(direction.x, max(direction.y, 0.0), direction.z);
        level = dot(level, level) > 1e-8 ? normalize(level) : vec3(1.0, 0.0, 0.0);
        color = skyBehind(sky, level, frame.skySun) * frame.backgroundRadiance.a;
    }

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
