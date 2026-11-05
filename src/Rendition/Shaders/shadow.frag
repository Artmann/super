#version 460
#extension GL_EXT_nonuniform_qualifier : require
#extension GL_GOOGLE_include_directive : require
#include "material.glsl"

layout(location = 0) in vec2 inUv;

// A 4x4 ordered pattern, so glass lets a share of the light through evenly.
const float DITHER[16] = float[16](
    0.0, 8.0, 2.0, 10.0,
    12.0, 4.0, 14.0, 6.0,
    3.0, 11.0, 1.0, 9.0,
    15.0, 7.0, 13.0, 5.0);

// The most light even perfectly clear glass lets straight through. Bending spreads the
// rest, which is why a glass ball still casts a shadow.
const float GLASS_LIGHT = 0.75;

// Cut-out holes, such as grille meshes, let light through. Opacity is worked out as in
// materialSurface() in surface.glsl. Glass leaves out as many texels as light it lets
// through, and the soft shadow filter and anti-aliasing smooth the pattern into a
// lighter shadow.
void main() {
    Material material = materials[draw.material];
    float opacity = texture(textures[material.baseColorTexture], inUv).a * material.baseColor.a
        * texture(textures[material.surfaceTexture], inUv).a;
    if (opacity < material.alphaCutoff) {
        discard;
    }

    if ((material.flags & MATERIAL_TRANSPARENT) != 0u) {
        ivec2 texel = ivec2(gl_FragCoord.xy) & 3;
        float threshold = (DITHER[texel.y * 4 + texel.x] + 0.5) / 16.0;
        if (threshold < (1.0 - opacity) * GLASS_LIGHT) {
            discard;
        }
    }
}
