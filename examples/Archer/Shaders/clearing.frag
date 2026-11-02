#version 460
#extension GL_GOOGLE_include_directive : require
#include "surface.glsl"

// The clearing's ground: grass in the open, needles and earth under the firs, and a
// patch of bare, trodden leaves where Erika stands. Colour comes from texture array 0 and
// normals from array 1, layer 0 grass, 1 forest floor, 2 leaf litter.
// shaderParameters[0] holds the clearing's middle in x and z, then where the forest floor
// starts and where it has taken over, in metres from the middle.
// shaderParameters[1] holds the worn patch's middle in x and z and its radius.

float hash(vec2 p) {
    return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), u.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), u.x), u.y);
}

float clouds(vec2 p) {
    return noise(p) * 0.5 + noise(p * 2.03 + 17.0) * 0.3 + noise(p * 4.1 + 41.0) * 0.2;
}

// Two scales of the same layer, so the repeat doesn't show across the open ground.
vec4 layer(int slot, float index) {
    return mix(shaderLayer(slot, inUv, index), shaderLayer(slot, inUv * 0.37 + 0.21, index), 0.4);
}

void main() {
    Material material = materials[draw.material];
    vec4 clearing = material.shaderParameters[0];
    vec4 worn = material.shaderParameters[1];

    vec2 at = inPosition.xz;
    float wobble = clouds(at * 0.12) - 0.5;
    float fromMiddle = length(at - clearing.xy) + wobble * 9.0;
    float forest = smoothstep(clearing.z, clearing.w, fromMiddle);
    // Trodden in the middle, thinning out through the grass, never quite bare.
    float bare = 0.8 * (1.0 - smoothstep(worn.z * 0.15, worn.z * 1.2, length(at - worn.xy) + wobble * 3.0));
    bare *= smoothstep(0.25, 0.55, clouds(at * 0.6));
    // A few drifts of leaves in the grass, so the open ground isn't one carpet.
    float drift = smoothstep(0.66, 0.85, clouds(at * 0.09 + 5.0)) * (1.0 - forest);

    float weights[3];
    weights[2] = max(bare, drift * 0.6);
    weights[1] = forest * (1.0 - weights[2]);
    weights[0] = (1.0 - forest) * (1.0 - weights[2]);

    vec3 color = vec3(0.0);
    vec3 bump = vec3(0.0);
    for (int i = 0; i < 3; i++) {
        vec3 c = layer(0, float(i)).rgb;
        if (i == 1) {
            // The forest floor photo is grey; warm light turns that pink, so it's pushed to moss and earth.
            c *= vec3(0.85, 0.9, 0.62);
        }
        if (i == 2) {
            // The leaf litter is autumn orange; old, trodden leaves are browner and darker.
            c = mix(c, vec3(dot(c, vec3(0.3, 0.55, 0.15))), 0.45) * vec3(0.72, 0.68, 0.6);
        }
        color += c * weights[i];
        bump += (layer(1, float(i)).xyz * 2.0 - 1.0) * weights[i];
    }

    // Slow patches of lusher and sun-dried grass.
    float dry = clouds(at * 0.05 + 9.0);
    vec3 grassTint = mix(vec3(0.85, 1.0, 0.7), vec3(1.15, 1.0, 0.65), dry);
    color *= mix(vec3(1.0), grassTint * material.baseColor.rgb, weights[0]);
    // Under the firs the floor gets little light; the sun alone can't make that dark.
    color *= mix(1.0, 0.35, forest);

    vec3 n = normalize(gl_FrontFacing ? inNormal : -inNormal);
    vec3 t = normalize(inTangent.xyz - n * dot(n, inTangent.xyz));
    vec3 b = cross(n, t) * inTangent.w;

    Surface surface;
    surface.baseColor = color;
    surface.metallic = 0.0;
    surface.roughness = material.roughness;
    surface.normal = normalize(t * bump.x + b * bump.y + n * bump.z);
    surface.emissive = vec3(0.0);
    surface.clearcoat = 0.0;
    writeSurface(surface);
}
