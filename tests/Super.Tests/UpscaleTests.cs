using System.Numerics;
using Rendition;
using Rendition.Passes;

namespace Super.Tests;

public class UpscaleTests
{
    [Theory]
    // ceil(8 * (1 / scale)^2): 8 at native size, 32 at half size each way, and 8 / 0.67^2 = 17.8 rounds up to 18.
    [InlineData(1f, 8)]
    [InlineData(0.5f, 32)]
    [InlineData(0.67f, 18)]
    [InlineData(0.75f, 15)]
    public void Jitter_phase_count_follows_the_scale(float scale, int phases)
    {
        Assert.Equal(phases, Upscale.JitterPhaseCount(scale));
    }

    [Fact]
    public void Jitter_is_halton_two_three()
    {
        // Halton base 2 runs 1/2, 1/4, 3/4 and base 3 runs 1/3, 2/3, 1/9, each less half a pixel.
        AssertNear(new Vector2(0f, -1f / 6f), Upscale.Jitter(0, 8));
        AssertNear(new Vector2(-0.25f, 1f / 6f), Upscale.Jitter(1, 8));
        AssertNear(new Vector2(0.25f, -7f / 18f), Upscale.Jitter(2, 8));
    }

    [Fact]
    public void Jitter_starts_again_after_its_phases()
    {
        AssertNear(Upscale.Jitter(1, 8), Upscale.Jitter(9, 8));
        // With 32 phases, frame 9 is the ninth point: Halton(10, 2) = 5/16 and Halton(10, 3) = 10/27.
        AssertNear(new Vector2(5f / 16f - 0.5f, 10f / 27f - 0.5f), Upscale.Jitter(9, 32));
    }

    [Fact]
    public void A_missing_library_falls_back_to_taa()
    {
        var log = new StringWriter();

        Assert.Equal(Upscaler.Taa, Upscale.Choose(Upscaler.Fsr, libraryLoaded: false, log));
        Assert.Equal(
            "FSR upscaling was asked for, but amd_fidelityfx_vk could not be loaded, so anti-aliasing uses TAA instead.",
            log.ToString().TrimEnd());
    }

    [Fact]
    public void Fsr_is_used_when_its_library_loads()
    {
        var log = new StringWriter();

        Assert.Equal(Upscaler.Fsr, Upscale.Choose(Upscaler.Fsr, libraryLoaded: true, log));
        Assert.Equal("", log.ToString());
    }

    [Fact]
    public void Taa_needs_no_library_and_says_nothing()
    {
        var log = new StringWriter();

        Assert.Equal(Upscaler.Taa, Upscale.Choose(Upscaler.Taa, libraryLoaded: false, log));
        Assert.Equal("", log.ToString());
    }

    private const string LumaHistoryMessage =
        "vkCmdDispatch(): the storage image descriptor [VkDescriptorSet 0x1, Set 0, Binding 7, Index 0, variable \"rw_luma_history\"] "
        + "is accessed by a OpTypeImage that has a Format operand Rgba8 (equivalent to VK_FORMAT_R8G8B8A8_UNORM) which doesn't match "
        + "the VkImageView 0x2 format (VK_FORMAT_R16G16B16A16_SFLOAT).";

    [Fact]
    public void Fsrs_own_luma_history_format_message_is_not_counted()
    {
        Assert.True(Rendition.Vulkan.VulkanContext.IsFsrLumaHistoryFormat(LumaHistoryMessage));
    }

    [Theory]
    [InlineData("\"rw_luma_history\"", "\"rw_upscaled_output\"")]
    [InlineData("Format operand Rgba8", "Format operand Rgba16f")]
    [InlineData("VK_FORMAT_R16G16B16A16_SFLOAT", "VK_FORMAT_R32G32B32A32_SFLOAT")]
    public void Any_other_format_message_is_still_counted(string part, string replacement)
    {
        Assert.False(Rendition.Vulkan.VulkanContext.IsFsrLumaHistoryFormat(LumaHistoryMessage.Replace(part, replacement)));
    }

    private static void AssertNear(Vector2 expected, Vector2 actual)
    {
        Assert.Equal(expected.X, actual.X, 1e-6f);
        Assert.Equal(expected.Y, actual.Y, 1e-6f);
    }
}
