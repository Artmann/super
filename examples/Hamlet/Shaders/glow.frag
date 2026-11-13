#version 460
#extension GL_GOOGLE_include_directive : require
#include "surface.glsl"

// A surface that can glow, such as a lamp's bulb or a lit window. A generated surface has no
// glow of its own, so the draw's tint carries it: a tint of 1 is the plain material, and
// every step above 1 adds that much glow. The tint is divided back out of the colour, so
// the surface keeps its own colour lit or not.
// shaderParameters[0] holds the glow's colour in rgb and its strength in w.

void main() {
    Material material = materials[draw.material];
    vec4 glow = material.shaderParameters[0];

    Surface surface = materialSurface();
    float amount = max(draw.tint.r - 1.0, 0.0);
    surface.baseColor /= max(draw.tint.rgb, vec3(1.0));
    surface.emissive += glow.rgb * glow.w * amount;
    writeSurface(surface);
}
