// Being underwater: water swallows light with distance, red first, and the rippling
// surface focuses sunlight into moving nets of light (caustics) and hazy shafts.
// Needs common.glsl and sampling.glsl.

const float NOTHING_DRAWN_DISTANCE = 1000.0;
const int SHAFT_STEPS = 14;

// The shafts are a blurred, larger version of the pattern on the floor.
const float SHAFT_PATTERN_SCALE = 0.3;

bool isUnderwater() {
    return frame.waterAbsorption.a > 0.5;
}

// Bright thin lines that wander over time. Returns roughly 0 to 1.
float causticPattern(vec2 uv, float time) {
    const float TAU = 6.28318530718;
    const float SOFTNESS = 0.005;
    const int LAYERS = 4;

    vec2 p = mod(uv * TAU, TAU) - 250.0;
    vec2 wander = p;
    float lines = 1.0;
    for (int n = 0; n < LAYERS; n++) {
        float t = time * (1.0 - 3.5 / float(n + 1));
        wander = p + vec2(cos(t - wander.x) + sin(t + wander.y), sin(t - wander.y) + cos(t + wander.x));
        lines += 1.0 / length(vec2(p.x / (sin(wander.x + t) / SOFTNESS), p.y / (cos(wander.y + t) / SOFTNESS)));
    }

    lines = 1.17 - pow(lines / float(LAYERS), 1.4);
    return clamp(pow(abs(lines), 8.0), 0.0, 1.5);
}

float metresToSurface(vec3 position, vec3 l) {
    return max(frame.waterShallow.a - position.y, 0.0) / max(l.y, 0.1);
}

// Follows the light up to the surface, because that's where the pattern is made.
float causticAt(vec3 position, vec3 l, float scale) {
    vec3 surface = position + l * metresToSurface(position, l);
    return causticPattern(surface.xz * scale, frame.time * 0.6);
}

vec3 waterAbsorbed(float metres) {
    return exp(-frame.waterAbsorption.rgb * metres);
}

// The sunlight left at a point after coming down through the water.
vec3 sunlightReaching(vec3 position, vec3 l, int light) {
    vec3 radiance = frame.lights[light].radiance.rgb;
    if (!isUnderwater()) {
        return radiance;
    }

    float caustic = causticAt(position, l, frame.waterLight.y);
    return radiance * waterAbsorbed(metresToSurface(position, l)) * (1.0 + frame.waterLight.x * caustic);
}

// The colour of open water in a direction: dark below, bright towards the surface and sun.
vec3 waterColor(vec3 direction) {
    vec3 color = mix(frame.waterDeep.rgb, frame.waterShallow.rgb, smoothstep(-0.35, 0.75, direction.y));
    if (frame.lightCount > 0) {
        float glow = pow(max(dot(direction, frame.lights[0].direction.xyz), 0.0), 12.0);
        color += frame.waterShallow.rgb * glow * 1.5;
    }

    return color;
}

// Sums the shafts of light the view passes through on its way to a point.
vec3 lightShafts(vec3 direction, float metres) {
    if (frame.waterDeep.a <= 0.0 || frame.lightCount == 0) {
        return vec3(0.0);
    }

    vec3 l = frame.lights[0].direction.xyz;
    float stepLength = min(metres, frame.waterLight.z) / float(SHAFT_STEPS);
    float noise = frameNoise(gl_FragCoord.xy, frame.frameNumber);
    vec3 sum = vec3(0.0);
    for (int i = 0; i < SHAFT_STEPS; i++) {
        float travelled = (float(i) + noise) * stepLength;
        vec3 point = frame.cameraPosition.xyz + direction * travelled;
        float pattern = causticAt(point, l, frame.waterLight.y * SHAFT_PATTERN_SCALE);
        sum += pattern * waterAbsorbed(travelled + metresToSurface(point, l));
    }

    float towardsSun = 0.5 + 0.5 * dot(direction, l);
    return sum * stepLength * frame.waterDeep.a * towardsSun * frame.waterShallow.rgb;
}

// What the camera sees of something, past all the water in front of it.
vec3 throughWater(vec3 color, vec3 position) {
    vec3 ray = position - frame.cameraPosition.xyz;
    float metres = length(ray);
    vec3 direction = ray / metres;
    vec3 kept = waterAbsorbed(metres);
    return color * kept + waterColor(direction) * (1.0 - kept) + lightShafts(direction, metres);
}
