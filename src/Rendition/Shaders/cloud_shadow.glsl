// The clouds' shadow on the ground and in the air, from the map cloud_shadow.comp draws round the
// camera each frame. Needs common.glsl. Included through water.glsl by the shaders that light
// by the sun: the lighting pass, the particles and the volumetric fog. See "Clouds" in
// docs/design.md.

layout(set = 0, binding = 14) uniform sampler3D cloudShadowTexture;

// How much of light's sunlight the clouds let through to a point: 1 for every light but the one that casts their
// shadow. The map holds the ground's, at height 0, so a point above it looks up where the light comes from. Towards
// the map's edge the shadow fades to the clouds' average, so the edge doesn't show on distant hills.
float cloudShadowAt(vec3 position, int light) {
    if (light != int(frame.cloudShadow.w)) {
        return 1.0;
    }

    vec3 l = frame.lights[light].direction.xyz;
    vec2 ground = position.xz - l.xz * (position.y / max(l.y, 0.1));
    vec2 uv = (ground - frame.cameraPosition.xz - frame.cloudShadow.xy) / (2.0 * frame.cloudShadow.z) + 0.5;
    float shadow = textureLod(cloudShadowTexture, vec3(uv, 0.5), 0.0).r;
    vec2 edge = abs(uv - 0.5) * 2.0;
    float fade = smoothstep(0.75, 1.0, max(edge.x, edge.y));
    return mix(shadow, 1.0 - 0.5 * frame.cloudShape.x, fade);
}
