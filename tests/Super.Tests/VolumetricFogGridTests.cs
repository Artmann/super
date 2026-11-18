using Rendition;
using Rendition.Passes;

namespace Super.Tests;

/// <summary>The volumetric fog's grid: how many cells it has, which a game picks to trade detail for cost.</summary>
public class VolumetricFogGridTests
{
    [Fact]
    public void Grid_takes_its_cell_size_and_slices_from_the_settings()
    {
        Assert.Equal((100, 57, 32), VolumetricFog.GridSize(1600, 900, cellPixels: 16, slices: 32));
        Assert.Equal((400, 225, 128), VolumetricFog.GridSize(1600, 900, cellPixels: 4, slices: 128));
    }

    [Fact]
    public void The_default_grid_is_8_pixels_a_cell_and_64_slices()
    {
        var graphics = new Graphics();

        Assert.Equal(8, graphics.Atmosphere.VolumetricFogCellSize);
        Assert.Equal(64, graphics.Atmosphere.VolumetricFogSlices);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(33)]
    public void Cell_size_outside_4_to_32_is_refused(int cellSize)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Graphics { Atmosphere = { VolumetricFogCellSize = cellSize } });

        Assert.StartsWith("A volumetric fog cell can be from 4 to 32 pixels across.", error.Message);
        Assert.Equal("value", error.ParamName);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(129)]
    public void Slices_outside_16_to_128_is_refused(int slices)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => new Graphics { Atmosphere = { VolumetricFogSlices = slices } });

        Assert.StartsWith("The volumetric fog can have from 16 to 128 slices.", error.Message);
        Assert.Equal("value", error.ParamName);
    }

    [Fact]
    public void Ultra_cuts_the_fog_into_128_slices()
    {
        Assert.Equal(128, Graphics.For(GraphicsQuality.Ultra).Atmosphere.VolumetricFogSlices);
        Assert.Equal(64, Graphics.For(GraphicsQuality.High).Atmosphere.VolumetricFogSlices);
        Assert.Equal(8, Graphics.For(GraphicsQuality.Ultra).Atmosphere.VolumetricFogCellSize);
    }

    [Fact]
    public void The_renderer_is_told_the_grid()
    {
        var graphics = new Graphics { Atmosphere = { VolumetricFogCellSize = 16, VolumetricFogSlices = 32 } };

        var settings = WindowFrames.RendererSettings(graphics, hidden: false);

        Assert.Equal(16, settings.VolumetricFogCellSize);
        Assert.Equal(32, settings.VolumetricFogSlices);
    }
}
