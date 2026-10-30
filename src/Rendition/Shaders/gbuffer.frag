#version 460
#extension GL_GOOGLE_include_directive : require
#include "surface.glsl"

void main() {
    writeSurface(materialSurface());
}
