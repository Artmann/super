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
            + 4 * scalar        // flags, transmission, heightTexture, heightDepth
            + 4 * scalar        // detailColorTexture, detailNormalTexture, detailScale, ior
            + vec4              // subsurface
            + 4 * scalar        // maskTexture, maskHeight, wearRoughness, dirtRoughness
            + vec4              // wear
            + vec4;             // dirt

        Assert.Equal(expected, Unsafe.SizeOf<ResourceTable.GpuMaterial>());
    }

    // 64 bytes of fixed values, 128 of shader parameters, 16 and 16 of slots, then the
    // flags, transmission, the height and detail fields and ior, four bytes each, then subsurface.
    // Subsurface is a vec4, so it ends at 272. The mask's four scalars follow at 272, 276, 280 and
    // 284, which leaves wear on a 16-byte boundary at 288 and dirt after it at 304, ending at 320.
    [Theory]
    [InlineData(nameof(ResourceTable.GpuMaterial.Flags), 224)]
    [InlineData(nameof(ResourceTable.GpuMaterial.Transmission), 228)]
    [InlineData(nameof(ResourceTable.GpuMaterial.HeightTexture), 232)]
    [InlineData(nameof(ResourceTable.GpuMaterial.HeightDepth), 236)]
    [InlineData(nameof(ResourceTable.GpuMaterial.DetailColorTexture), 240)]
    [InlineData(nameof(ResourceTable.GpuMaterial.DetailNormalTexture), 244)]
    [InlineData(nameof(ResourceTable.GpuMaterial.DetailScale), 248)]
    [InlineData(nameof(ResourceTable.GpuMaterial.Ior), 252)]
    [InlineData(nameof(ResourceTable.GpuMaterial.Subsurface), 256)]
    [InlineData(nameof(ResourceTable.GpuMaterial.MaskTexture), 272)]
    [InlineData(nameof(ResourceTable.GpuMaterial.MaskHeight), 276)]
    [InlineData(nameof(ResourceTable.GpuMaterial.WearRoughness), 280)]
    [InlineData(nameof(ResourceTable.GpuMaterial.DirtRoughness), 284)]
    [InlineData(nameof(ResourceTable.GpuMaterial.Wear), 288)]
    [InlineData(nameof(ResourceTable.GpuMaterial.Dirt), 304)]
    public void Field_sits_where_material_glsl_reads_it(string field, int offset)
    {
        Assert.Equal(offset, (int)Marshal.OffsetOf<ResourceTable.GpuMaterial>(field));
    }

    [Fact]
    public void The_scene_vertex_layout_feeds_the_second_uv_set_to_location_6()
    {
        var attribute = Assert.Single(SceneDraws.VertexAttributes, a => a.Location == 6);
        Assert.Equal(Silk.NET.Vulkan.Format.R32G32Sfloat, attribute.Format);
        // Position 12, normal 12, tangent 16, uv 8, joints 4 and weights 4 come first.
        Assert.Equal(56u, attribute.Offset);
        Assert.Equal(64, Unsafe.SizeOf<Rendition.Vertex>());
    }

    [Fact]
    public void Draw_constants_match_the_draw_block_in_material_glsl()
    {
        // vec4 tint, vec4 ghost, int material, three swim floats, int cascade, int firstBone. Each
        // copy's matrix is in the instance buffer, not here.
        Assert.Equal(0, (int)Marshal.OffsetOf<SceneDraws.DrawConstants>(nameof(SceneDraws.DrawConstants.Tint)));
        Assert.Equal(16, (int)Marshal.OffsetOf<SceneDraws.DrawConstants>(nameof(SceneDraws.DrawConstants.Ghost)));
        Assert.Equal(32, (int)Marshal.OffsetOf<SceneDraws.DrawConstants>(nameof(SceneDraws.DrawConstants.Material)));
        Assert.Equal(48, (int)Marshal.OffsetOf<SceneDraws.DrawConstants>(nameof(SceneDraws.DrawConstants.Cascade)));
        Assert.Equal(52, (int)Marshal.OffsetOf<SceneDraws.DrawConstants>(nameof(SceneDraws.DrawConstants.FirstBone)));
        Assert.Equal(56, Unsafe.SizeOf<SceneDraws.DrawConstants>());
    }
}
