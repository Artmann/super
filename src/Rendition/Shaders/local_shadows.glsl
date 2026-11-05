// Shadows of point and spot lights, from the shared atlas. Needs common.glsl,
// light_data.glsl, brdf.glsl and sampling.glsl.

layout(set = 0, binding = 6) uniform sampler2DShadow shadowAtlasCompare;
layout(set = 0, binding = 7) uniform sampler2D shadowAtlasDepth;

// Every local light is treated as this wide, in metres, for the softness of its shadows:
// about a candle's flame or a lamp's bulb.
const float LOCAL_LIGHT_SIZE = 0.08;
const int LOCAL_BLOCKER_SAMPLES = 12;
const int LOCAL_SHADOW_SAMPLES = 16;

// The tile that sees a point: a spot has one, and a point light six, one per face of a
// cube in the order +x, -x, +y, -y, +z, -z. Each face sees a little past its edges, so the
// filter round a point stays inside it.
int localShadowTile(LocalLight light, vec3 position) {
    int first = int(light.radianceShadow.w);
    if (first < 0 || light.directionCone.w > -1.5) {
        return first;
    }

    vec3 d = position - light.positionRange.xyz;
    vec3 a = abs(d);
    int face = a.x >= a.y && a.x >= a.z ? (d.x > 0.0 ? 0 : 1) : a.y >= a.z ? (d.y > 0.0 ? 2 : 3) : (d.z > 0.0 ? 4 : 5);
    return first + face;
}

// The distance from the light along its view that a reversed-Z depth stands for, and back.
float tileDistance(float stored, ShadowTile tile) {
    float n = tile.depth.x;
    float f = tile.depth.y;
    return n * f / (stored * (f - n) + n);
}

float tileDepth(float away, ShadowTile tile) {
    float n = tile.depth.x;
    float f = tile.depth.y;
    return n * (f - away) / (away * (f - n));
}

// Soft shadow that is sharp where an object touches the surface and blurs with distance,
// as the suns' is: blockers are found first, and how far they are from the surface sets
// the blur. Returns 1 for fully lit.
float localShadow(LocalLight light, vec3 position, vec3 n, vec3 l) {
    int index = localShadowTile(light, position);
    if (index < 0) {
        return 1.0;
    }

    ShadowTile tile = shadowTiles[index];
    float atlasTexel = 1.0 / float(textureSize(shadowAtlasDepth, 0).x);
    float tileTexels = tile.rect.z / atlasTexel;
    float nDotL = clamp(dot(n, l), 0.05, 1.0);
    float slope = min(sqrt(1.0 - nDotL * nDotL) / nDotL, 2.0);

    // Moving the point a texel or two off its surface stops it shadowing itself.
    float away = (tile.matrix * vec4(position, 1.0)).w;
    float texelMetres = 2.0 * away / (tile.depth.z * tileTexels);
    vec4 projected = tile.matrix * vec4(position + n * texelMetres * 1.5, 1.0);
    float receiver = projected.w;
    vec2 uv = tile.rect.xy + (projected.xy / receiver * 0.5 + 0.5) * tile.rect.zw;
    vec2 low = tile.rect.xy + 0.5 * atlasTexel;
    vec2 high = tile.rect.xy + tile.rect.zw - 0.5 * atlasTexel;

    float uvPerMetre = tile.depth.z * tile.rect.z / (2.0 * receiver);
    float searchRadius = clamp(2.0 * LOCAL_LIGHT_SIZE * uvPerMetre, 1.5 * atlasTexel, 0.05 * tile.rect.z);
    float rotation = frameNoise(gl_FragCoord.xy, frame.frameNumber) * 2.0 * PI;
    float bias = texelMetres * (1.0 + 2.0 * slope);

    float blockerSum = 0.0;
    int blockers = 0;
    for (int i = 0; i < LOCAL_BLOCKER_SAMPLES; i++) {
        vec2 offset = diskSample(i, LOCAL_BLOCKER_SAMPLES, rotation) * searchRadius;
        float stored = texture(shadowAtlasDepth, clamp(uv + offset, low, high)).r;
        float blocker = tileDistance(stored, tile);
        if (stored > 0.0 && blocker < receiver - bias - searchRadius / uvPerMetre * slope) {
            blockerSum += blocker;
            blockers++;
        }
    }
    if (blockers == 0) {
        return 1.0;
    }

    float blocker = blockerSum / float(blockers);
    float penumbra = clamp(0.5 * LOCAL_LIGHT_SIZE * (receiver - blocker) / blocker * uvPerMetre, atlasTexel, searchRadius);
    float reference = tileDepth(max(receiver - bias - penumbra / uvPerMetre * slope, tile.depth.x), tile);
    float visibility = 0.0;
    for (int i = 0; i < LOCAL_SHADOW_SAMPLES; i++) {
        vec2 offset = diskSample(i, LOCAL_SHADOW_SAMPLES, rotation) * penumbra;
        visibility += texture(shadowAtlasCompare, vec3(clamp(uv + offset, low, high), reference));
    }
    return visibility / float(LOCAL_SHADOW_SAMPLES);
}
