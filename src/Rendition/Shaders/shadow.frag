#version 460
#extension GL_EXT_nonuniform_qualifier : require
#extension GL_GOOGLE_include_directive : require
#include "material.glsl"

layout(location = 0) in vec2 inUv;

// Cut-out holes, such as grille meshes, let light through.
void main() {
    Material material = materials[draw.material];
    if (texture(textures[material.surfaceTexture], inUv).a < material.alphaCutoff) {
        discard;
    }
}
