using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Super.Tests;

public class GpuMaterialTests
{
    [Fact]
    public void Matches_the_std430_layout_of_the_material_struct_in_material_glsl()
    {
        const int vec4 = 16;
        const int scalar = 4;
        int expected =
            vec4                // baseColor
            + 4 * scalar        // metallic, roughness, alphaCutoff, baseColorTexture
            + 4 * scalar        // normalTexture, surfaceTexture, emissiveTexture, clearcoat
            + vec4              // emissive
            + 8 * vec4          // shaderParameters[8]
            + 4 * scalar        // shaderTextures[4]
            + 4 * scalar        // shaderArrays[4]
            + vec4;             // flags, rounded up to the struct's vec4 alignment

        Assert.Equal(expected, Unsafe.SizeOf<ResourceTable.GpuMaterial>());
    }

    [Fact]
    public void Flags_sit_right_after_the_shader_arrays_as_in_material_glsl()
    {
        // 64 bytes of fixed values, 128 of shader parameters, then 16 and 16 of slots.
        Assert.Equal(224, (int)Marshal.OffsetOf<ResourceTable.GpuMaterial>(nameof(ResourceTable.GpuMaterial.Flags)));
    }

    [Fact]
    public void Draw_constants_match_the_draw_block_in_material_glsl()
    {
        // mat4 model, vec4 tint, vec4 ghost, int material, three swim floats, int cascade, int firstBone.
        Assert.Equal(64, (int)Marshal.OffsetOf<SceneDraws.DrawConstants>(nameof(SceneDraws.DrawConstants.Tint)));
        Assert.Equal(80, (int)Marshal.OffsetOf<SceneDraws.DrawConstants>(nameof(SceneDraws.DrawConstants.Ghost)));
        Assert.Equal(96, (int)Marshal.OffsetOf<SceneDraws.DrawConstants>(nameof(SceneDraws.DrawConstants.Material)));
        Assert.Equal(112, (int)Marshal.OffsetOf<SceneDraws.DrawConstants>(nameof(SceneDraws.DrawConstants.Cascade)));
        Assert.Equal(116, (int)Marshal.OffsetOf<SceneDraws.DrawConstants>(nameof(SceneDraws.DrawConstants.FirstBone)));
        Assert.Equal(120, Unsafe.SizeOf<SceneDraws.DrawConstants>());
    }
}
