using Rendition;

namespace Super.Tests;

public class RenderEffectsTests
{
    [Fact]
    public void The_shaders_subsurface_switch_is_bit_31_of_RenderEffects()
    {
        string common = File.ReadAllText(Path.Combine(WindTests.ShaderFolder(), "common.glsl"));

        Assert.Contains("const int OFF_SUBSURFACE = int(0x80000000u);", common);
        Assert.Equal(1L << 31, (long)RenderEffects.Subsurface);
    }

    [Fact]
    public void No_effect_uses_bit_30_which_is_the_renderers_debug_view_bit()
    {
        var effects = Enum.GetValues<RenderEffects>();

        Assert.All(effects, effect => Assert.Equal(0L, (long)effect & (1L << 30)));
    }

    [Fact]
    public void Every_named_effect_is_inside_All()
    {
        var effects = Enum.GetValues<RenderEffects>().Where(effect => effect != RenderEffects.None);

        Assert.All(effects, effect => Assert.Equal(effect, RenderEffects.All & effect));
    }
}
