#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"
#include "brdf.glsl"
#include "sampling.glsl"
#include "water.glsl"
#include "light_data.glsl"
#include "sky.glsl"

layout(location = 0) in vec2 inCorner;
layout(location = 1) in vec2 inScreenCorner;
layout(location = 2) in vec4 inColor;
layout(location = 3) in vec3 inPosition;
layout(location = 4) in float inSunlight;
layout(location = 5) flat in vec3 inPuff; // x: width; y: variant; z: glow

layout(set = 1, binding = 0) uniform sampler2D depthTexture;
layout(set = 1, binding = 2) uniform samplerCube irradianceTexture;
layout(set = 1, binding = 3) uniform samplerCube reflectionTexture;

// Must match ParticlePass.CameraConstants.
layout(push_constant) uniform Camera {
    vec4 right;
    vec4 up;
    vec4 forward;
} camera;

layout(location = 0) out vec4 outColor;

// A puff lit from the side looks round, but light also soaks through it, so it's never
// as dark on its shaded side as a solid ball would be.
const float ROUNDNESS = 0.6;
const float SCATTER = 1.3;

float hash(vec2 p) {
    p = fract(p * vec2(123.34, 456.21));
    p += dot(p, p + 45.32);
    return fract(p.x * p.y);
}

// Smooth noise from 0 to 1.
float valueNoise(vec2 p) {
    vec2 cell = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    float a = hash(cell);
    float b = hash(cell + vec2(1.0, 0.0));
    float c = hash(cell + vec2(0.0, 1.0));
    float d = hash(cell + vec2(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

void main() {
    float r2 = dot(inCorner, inCorner);
    if (r2 >= 1.0) {
        discard;
    }

    // A soft ball with a lumpy edge: noise eats into it more towards the outside.
    vec2 seed = vec2(inPuff.y * 17.13, inPuff.y * 7.31);
    float lumps = valueNoise(inCorner * 1.7 + seed) * 0.65 + valueNoise(inCorner * 3.9 - seed) * 0.35;
    float falloff = 1.0 - r2;
    float shape = clamp(falloff * falloff * (0.45 + 1.1 * lumps) - 0.05 * r2, 0.0, 1.0);

    // Melt into the surface behind instead of cutting through it, and thin out as the
    // camera comes close enough to be inside the puff.
    float width = inPuff.x;
    float along = dot(inPosition - frame.cameraPosition.xyz, camera.forward.xyz);
    float soft = clamp(along / width - 0.25, 0.0, 1.0);
    vec2 uv = gl_FragCoord.xy / vec2(textureSize(depthTexture, 0));
    float depth = texture(depthTexture, uv).r;
    if (depth > 0.0) {
        vec4 behind = frame.inverseViewProjection * vec4(uv * 2.0 - 1.0, depth, 1.0);
        float sceneDistance = dot(behind.xyz / behind.w - frame.cameraPosition.xyz, camera.forward.xyz);
        soft *= clamp((sceneDistance - along) / (0.5 * width), 0.0, 1.0);
    }

    float alpha = inColor.a * shape * soft;
    if (alpha < 1.0 / 512.0) {
        discard;
    }

    vec2 across = inScreenCorner;
    vec3 n = normalize(camera.right.xyz * across.x + camera.up.xyz * across.y
        - camera.forward.xyz * sqrt(max(1.0 - r2, 0.0)));
    n = normalize(mix(-camera.forward.xyz, n, ROUNDNESS));

    vec3 light = texture(irradianceTexture, n).rgb * frame.backgroundRadiance.a;
    for (int i = 0; i < frame.lightCount; i++) {
        vec3 l = frame.lights[i].direction.xyz;
        float wrapped = clamp(dot(n, l) * 0.5 + 0.5, 0.0, 1.0);
        float shadow = frame.lights[i].direction.w > 0.5 ? inSunlight : 1.0;
        light += sunlightReaching(inPosition, l, i) * wrapped * shadow * SCATTER / PI;
    }

    // Point and spot lights from this pixel's cluster, without their shadows.
    uint cluster = clusterAt(gl_FragCoord.xy, inPosition);
    uint localCount = clusterLightCount(cluster);
    for (uint i = 0; i < localCount; i++) {
        LocalLight local = localLights[clusterLight(cluster, i)];
        vec3 l;
        float reaching = localLightReaching(local, inPosition, l);
        float wrapped = clamp(dot(n, l) * 0.5 + 0.5, 0.0, 1.0);
        light += local.radianceShadow.rgb * reaching * wrapped * SCATTER / PI;
    }

    // A glowing puff, such as a flame, gives off light of its own colour.
    vec3 color = inColor.rgb * (light + inPuff.z);
    vec3 away = inPosition - frame.cameraPosition.xyz;
    if (isUnderwater()) {
        // The shafts of light are left out: they'd be added once per puff.
        vec3 kept = waterAbsorbed(length(away));
        color = color * kept + waterColor(normalize(away)) * (1.0 - kept);
    } else {
        // The same haze as surfaces, so distant dust melts into the sky.
        float fadeStart = frame.parameters.x;
        float fadeEnd = frame.parameters.y;
        if (fadeEnd > fadeStart) {
            vec3 beyond = frame.backgroundRadiance.rgb;
            if (frame.hazeColor.a > 0.5) {
                beyond = frame.hazeColor.rgb;
            } else if (frame.showEnvironment == 1) {
                beyond = skyBehind(reflectionTexture, normalize(away), frame.skySun) * frame.backgroundRadiance.a;
            }

            color = mix(color, beyond, smoothstep(fadeStart, fadeEnd, hazeDistance(inPosition)));
        }
    }

    // Premultiplied, as the blend expects.
    outColor = vec4(color * alpha, alpha);
}
