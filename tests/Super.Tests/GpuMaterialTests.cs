using System.Runtime.CompilerServices;
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
            + 4 * scalar;       // shaderArrays[4]

        Assert.Equal(expected, Unsafe.SizeOf<ResourceTable.GpuMaterial>());
    }
}
