// Must match DrawConstants in Passes/SceneDraws.cs.
layout(push_constant) uniform Draw {
    mat4 model;
    int material;
} draw;

// Must match GpuMaterial in Vulkan/ResourceTable.cs.
struct Material {
    vec4 baseColor;
    float metallic;
    float roughness;
    float alphaCutoff;
    uint baseColorTexture;
    uint normalTexture;
    uint surfaceTexture;
    uint emissiveTexture;
    float clearcoat;
    vec4 emissive; // rgb
};

layout(set = 1, binding = 0) uniform sampler2D textures[];
layout(std430, set = 1, binding = 1) readonly buffer Materials {
    Material materials[];
};
