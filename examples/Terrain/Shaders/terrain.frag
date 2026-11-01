#version 460
#extension GL_GOOGLE_include_directive : require
#include "surface.glsl"

// Blends four ground layers: sand, grass, rock and snow. Colour comes from texture
// array 0 and normals from array 1, one layer each. The chunk's splat texture in slot 0
// says how grassy each spot is in its red channel. shaderParameters[0] holds the snow
// height and fade, then the rock steepness and fade. shaderParameters[1] holds the
// chunk's origin in x and z, one over its size and the splat's width in texels, to find
// a spot on the splat with its edge texels sitting on the chunk's edges.
void main() {
    Material material = materials[draw.material];
    vec4 thresholds = material.shaderParameters[0];
    vec4 chunk = material.shaderParameters[1];

    vec2 splatUv = ((inPosition.xz - chunk.xy) * chunk.z * (chunk.w - 1.0) + 0.5) / chunk.w;
    float grassy = shaderTexture(0, splatUv).r;

    vec3 n = normalize(gl_FrontFacing ? inNormal : -inNormal);
    float steep = 1.0 - n.y;
    float rock = smoothstep(thresholds.z, thresholds.z + thresholds.w, steep);
    float snow = smoothstep(thresholds.x, thresholds.x + thresholds.y, inPosition.y) * (1.0 - rock * 0.7);

    float weights[4];
    weights[0] = (1.0 - grassy) * (1.0 - rock) * (1.0 - snow);
    weights[1] = grassy * (1.0 - rock) * (1.0 - snow);
    weights[2] = rock * (1.0 - snow);
    weights[3] = snow;

    vec3 color = vec3(0.0);
    vec3 bump = vec3(0.0);
    for (int layer = 0; layer < 4; layer++) {
        color += shaderLayer(0, inUv, float(layer)).rgb * weights[layer];
        bump += (shaderLayer(1, inUv, float(layer)).xyz * 2.0 - 1.0) * weights[layer];
    }

    vec3 t = normalize(inTangent.xyz - n * dot(n, inTangent.xyz));
    vec3 b = cross(n, t) * inTangent.w;

    Surface surface;
    surface.baseColor = color * material.baseColor.rgb;
    surface.metallic = 0.0;
    surface.roughness = mix(material.roughness, 0.45, snow);
    surface.normal = normalize(t * bump.x + b * bump.y + n * bump.z);
    surface.emissive = vec3(0.0);
    surface.clearcoat = 0.0;
    writeSurface(surface);
}
