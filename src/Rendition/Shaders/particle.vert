#version 460
#extension GL_GOOGLE_include_directive : require
#include "common.glsl"

// One soft puff, as a square that faces the camera. Each corner looks up the shadow map
// on its own, so a puff half in shade is shaded across, and the fragment shader only
// blends between them.

layout(location = 0) in vec4 inPositionSize; // xyz: middle; w: width in metres
layout(location = 1) in vec4 inColor;        // rgb: linear colour; a: opacity
layout(location = 2) in vec3 inShape;        // x: rotation in radians; y: variant; z: glow

layout(set = 1, binding = 1) uniform sampler2DArrayShadow shadowCompareTexture;

// Must match ParticlePass.CameraConstants.
layout(push_constant) uniform Camera {
    vec4 right;
    vec4 up;
    vec4 forward;
} camera;

layout(location = 0) out vec2 outCorner;        // -1 to 1 across the puff, turning with it
layout(location = 1) out vec2 outScreenCorner;  // the same, lined up with the screen
layout(location = 2) out vec4 outColor;
layout(location = 3) out vec3 outPosition;      // this point on the square, in the world
layout(location = 4) out float outSunlight;     // how much the shadow-casting light reaches
layout(location = 5) flat out vec3 outPuff;     // x: width; y: variant; z: glow

const vec2 Corners[6] = vec2[](
    vec2(-1.0, -1.0), vec2(1.0, -1.0), vec2(-1.0, 1.0),
    vec2(-1.0, 1.0), vec2(1.0, -1.0), vec2(1.0, 1.0));

const int SHADOW_TAPS = 5;
const vec2 ShadowTaps[SHADOW_TAPS] = vec2[](
    vec2(0.0, 0.0), vec2(1.5, 0.5), vec2(-0.5, 1.5), vec2(-1.5, -0.5), vec2(0.5, -1.5));

// A few filtered taps at one point. Particles float in the air, so there's no surface to
// lift off, only a small allowance for the map's own blur.
float sunlightAt(vec3 position) {
    float texel = 1.0 / float(textureSize(shadowCompareTexture, 0).x);
    for (int c = 0; c < frame.shadowCascades; c++) {
        vec4 projected = frame.shadowMatrices[c] * vec4(position, 1.0);
        vec2 uv = projected.xy * 0.5 + 0.5;
        if (any(lessThan(uv, vec2(3.0 * texel))) || any(greaterThan(uv, vec2(1.0 - 3.0 * texel)))
            || projected.z <= 0.0 || projected.z >= 1.0) {
            continue;
        }

        // Reversed-Z: the point is lit when it's at least as near the light as the map.
        float reference = projected.z + 2.0 * texel;
        float lit = 0.0;
        for (int i = 0; i < SHADOW_TAPS; i++) {
            lit += texture(shadowCompareTexture, vec4(uv + ShadowTaps[i] * texel, float(c), reference));
        }
        return lit / float(SHADOW_TAPS);
    }
    return 1.0;
}

void main() {
    vec2 corner = Corners[gl_VertexIndex];
    float rotation = inShape.x;
    float c = cos(rotation);
    float s = sin(rotation);
    vec2 turned = vec2(c * corner.x - s * corner.y, s * corner.x + c * corner.y);

    float halfWidth = inPositionSize.w * 0.5;
    vec3 position = inPositionSize.xyz + (camera.right.xyz * turned.x + camera.up.xyz * turned.y) * halfWidth;

    outCorner = corner;
    outScreenCorner = turned;
    outColor = inColor;
    outPosition = position;
    outPuff = vec3(inPositionSize.w, inShape.y, inShape.z);

    outSunlight = 1.0;
    for (int i = 0; i < frame.lightCount; i++) {
        if (frame.lights[i].direction.w > 0.5) {
            outSunlight = sunlightAt(position);
        }
    }

    gl_Position = frame.viewProjection * vec4(position, 1.0);
}
