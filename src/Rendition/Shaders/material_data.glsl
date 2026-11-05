// The material table's layout, shared by the passes that draw meshes and the fullscreen
// passes that look materials up by index. Each declares the buffer in its own set.

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
    vec4 shaderParameters[8];
    uint shaderTextures[4];
    uint shaderArrays[4];
    uint flags; // MaterialFlags in Types.cs.
    float transmission; // How much light from behind shines through, for subsurface materials.
    uint heightTexture;
    float heightDepth; // Metres, 0 for no parallax.
    uint detailColorTexture;
    uint detailNormalTexture;
    float detailScale; // 0 for no detail textures.
    float ior; // 1 or more; 1 bends nothing.
    vec4 subsurface; // How far red, green and blue spread under the surface in metres; w is the widest, 0 for none.
};

const uint MATERIAL_TRANSPARENT = 2u;
const uint MATERIAL_FOLIAGE = 4u;
const uint MATERIAL_DETAIL_MASK = 8u;

// A G-buffer emissive alpha at or above this marks a subsurface pixel, holding the
// material's index plus this. Clearcoat stays within 0 to 1 and foliage is negative.
// Half floats hold whole numbers exactly up to 2048, which covers every material.
const float SUBSURFACE_MARK = 2.0;
