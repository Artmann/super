#version 460
#extension GL_GOOGLE_include_directive : require
#include "surface.glsl"

// Keeps the material as it is and sends bands of light across it. The material chooses
// the colour in shaderParameters[0].rgb and the speed in shaderParameters[0].a.
void main() {
    Surface surface = materialSurface();

    vec4 parameters = materials[draw.material].shaderParameters[0];
    float band = sin(inPosition.y * 14.0 - frame.time * parameters.a);
    surface.emissive += parameters.rgb * smoothstep(0.75, 1.0, band);

    writeSurface(surface);
}
