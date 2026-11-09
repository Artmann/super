#version 460
#extension GL_GOOGLE_include_directive : require
#extension GL_EXT_nonuniform_qualifier : require
#include "common.glsl"
#include "sampling.glsl"
#include "material_data.glsl"

layout(set = 1, binding = 0) uniform sampler2D textures[];
layout(std430, set = 1, binding = 1) readonly buffer Materials {
    Material materials[];
};

layout(set = 2, binding = 0) uniform sampler2D depthTexture;
layout(set = 2, binding = 1) uniform sampler2D normalCopy; // The normal target as the opaque surfaces left it.

layout(location = 0) flat in mat4 inInverse;
layout(location = 4) flat in vec4 inWeights; // Colour, normal and roughness weights, and the angle fade.
layout(location = 5) flat in vec3 inAcross;
layout(location = 6) flat in vec3 inUp;
layout(location = 7) flat in uint inMaterial;
layout(location = 8) flat in vec3 inAlong;

layout(location = 0) out vec4 outBaseColor;          // Blended by alpha; metallic is left alone.
layout(location = 1) out vec4 outNormalRoughnessAo;  // Written whole, mixed here with what was there.

// The decal fades out over the last part of its depth, so it doesn't stop at a hard line where
// the box ends on a curved or bumpy surface.
const float DEPTH_FADE_START = 0.4;

// How much steeper than a surface facing the camera the material's mip may be picked. At the
// edges of things the screen derivatives jump across to whatever is behind, which would pick the
// smallest mip and draw a line of blur.
const float MAX_SLOPE = 4.0;

vec3 boxPosition(vec2 pixel, float depth, vec2 size) {
    vec4 world = frame.inverseViewProjection * vec4(pixel / size * 2.0 - 1.0, depth, 1.0);
    return (inInverse * vec4(world.xyz / world.w, 1.0)).xyz;
}

// A tangent-space normal from a normal map's red and green, as surface.glsl reads them.
vec3 normalFrom(vec4 texel) {
    vec2 xy = texel.xy * 2.0 - 1.0;
    return vec3(xy, sqrt(max(1.0 - dot(xy, xy), 0.0)));
}

// Lays a detail normal over a base one, both in the same tangent space. This is
// reoriented normal mapping, as surface.glsl uses for detail maps.
vec3 blendNormals(vec3 base, vec3 detail) {
    vec3 t = base + vec3(0.0, 0.0, 1.0);
    vec3 u = detail * vec3(-1.0, -1.0, 1.0);
    return normalize(t * dot(t, u) / t.z - u);
}

void main() {
    vec2 size = vec2(textureSize(depthTexture, 0));
    ivec2 pixel = ivec2(gl_FragCoord.xy);
    float depth = texelFetch(depthTexture, pixel, 0).r;
    vec3 local = boxPosition(gl_FragCoord.xy, depth, size);

    // The far plane is infinitely far off, so a sky pixel has no position. It is put just outside
    // the box instead, so it can't feed infinities into its neighbours' derivatives below.
    bool sky = depth <= 0.0 || any(isnan(local)) || any(isinf(local));
    if (sky) {
        local = vec3(0.0, 0.0, 0.75);
    }

    // The material's width runs along the box's X and its height up the box's -Z, the way a
    // mesh's v runs against its bitangent. Derivatives are taken before any pixel leaves.
    vec2 uv = vec2(local.x + 0.5, 0.5 - local.z);
    vec2 uvX = dFdx(uv);
    vec2 uvY = dFdy(uv);

    // The same step across a surface facing the camera at this depth, which bounds the slope.
    vec3 flatX = sky ? vec3(0.0) : boxPosition(gl_FragCoord.xy + vec2(1.0, 0.0), depth, size) - local;
    vec3 flatY = sky ? vec3(0.0) : boxPosition(gl_FragCoord.xy + vec2(0.0, 1.0), depth, size) - local;
    float limit = MAX_SLOPE * max(length(flatX.xz), length(flatY.xz));
    uvX *= min(1.0, limit / max(length(uvX), 1e-8));
    uvY *= min(1.0, limit / max(length(uvY), 1e-8));
    float sharper = exp2(frame.mipBias);
    uvX *= sharper;
    uvY *= sharper;

    // Nothing was drawn at the far plane, so a box reaching into the sky has nothing to lie on there.
    // With materials off every surface is plain clay, decals included.
    if (sky || any(greaterThan(abs(local), vec3(0.5))) || isOff(OFF_MATERIALS)) {
        discard;
    }

    vec4 under = texelFetch(normalCopy, pixel, 0);
    vec3 normal = octahedralDecode(under.xy);

    // Surfaces turned away from the box's up take less of it, and none past the angle fade.
    vec3 up = normalize(inUp);
    float facing = dot(normal, up);
    float angleFade = inWeights.w;
    float reach = smoothstep(angleFade, angleFade + max((1.0 - angleFade) * 0.5, 1e-3), facing);
    reach *= 1.0 - smoothstep(DEPTH_FADE_START, 0.5, abs(local.y));
    if (reach <= 0.0) {
        discard;
    }

    Material material = materials[inMaterial];
    vec4 color = textureGrad(textures[material.baseColorTexture], uv, uvX, uvY) * material.baseColor;
    vec4 mask = textureGrad(textures[material.surfaceTexture], uv, uvX, uvY);
    float alpha = clamp(color.a * mask.a, 0.0, 1.0) * reach;
    if (alpha <= 0.0) {
        discard;
    }

    // The box's own frame, with its up as the third axis, is the tangent space both normals share:
    // the surface's normal as drawn and the decal's normal map, laid over it. As on a mesh, the
    // tangent runs the way u rises and the bitangent the way v falls, which is the box's +Z. It is
    // taken from the box rather than crossed from the other two, so a mirrored box keeps it.
    vec3 across = normalize(inAcross - up * dot(up, inAcross));
    vec3 along = inAlong - up * dot(up, inAlong);
    along = normalize(along - across * dot(across, along));
    vec3 base = vec3(dot(normal, across), dot(normal, along), facing);
    vec3 mapped = normalize(normalFrom(textureGrad(textures[material.normalTexture], uv, uvX, uvY)));
    vec3 blended = blendNormals(base, mapped);
    vec3 decalNormal = normalize(across * blended.x + along * blended.y + up * blended.z);
    vec3 finalNormal = normalize(mix(normal, decalNormal, alpha * inWeights.y));

    float roughness = mix(under.z, material.roughness * mask.g, alpha * inWeights.z);

    outBaseColor = vec4(color.rgb, alpha * inWeights.x);

    // The fourth channel marks the backs of one-sided surfaces for light probes, and passes through.
    outNormalRoughnessAo = vec4(octahedralEncode(finalNormal), roughness, under.w);
}
