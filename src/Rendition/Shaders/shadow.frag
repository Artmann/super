#version 460
#extension GL_EXT_nonuniform_qualifier : require
#extension GL_GOOGLE_include_directive : require
#include "material.glsl"

layout(location = 0) in vec2 inUv;

// Cut-out holes, such as grille meshes, let light through. Opacity is worked out as in
// materialSurface() in surface.glsl.
void main() {
    Material material = materials[draw.material];
    float opacity = texture(textures[material.baseColorTexture], inUv).a * material.baseColor.a
        * texture(textures[material.surfaceTexture], inUv).a;
    if (opacity < material.alphaCutoff) {
        discard;
    }
}
