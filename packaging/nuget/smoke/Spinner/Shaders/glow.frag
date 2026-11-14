#version 460
#extension GL_GOOGLE_include_directive : require
#include "surface.glsl"

// A plain surface that glows. shaderParameters[0].rgb is how brightly.
void main() {
    Surface surface = materialSurface();
    surface.emissive += materials[draw.material].shaderParameters[0].rgb;
    writeSurface(surface);
}
