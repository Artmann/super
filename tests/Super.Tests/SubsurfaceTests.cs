using System.Numerics;
using System.Runtime.InteropServices;
using Rendition;
using Rendition.Vulkan;

namespace Super.Tests;

public class SubsurfaceTests
{
    [Fact]
    public void Off_has_no_problem_even_with_clearcoat_and_foliage()
    {
        Assert.Null(default(Subsurface).Problem(clearcoat: 1f, MaterialFlags.Foliage));
    }

    [Fact]
    public void On_alone_has_no_problem()
    {
        Assert.Null(new Subsurface { Radius = 0.01f }.Problem(clearcoat: 0f, MaterialFlags.TwoSided));
    }

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(float.NaN)]
    public void Negative_or_missing_radius_is_a_problem(float radius)
    {
        Assert.Equal("subsurface radius must be 0 or more.", new Subsurface { Radius = radius }.Problem(0f, 0));
    }

    [Fact]
    public void Clearcoat_is_a_problem_because_it_shares_the_channel()
    {
        Assert.Equal("subsurface can't be combined with clearcoat, because they share a G-buffer channel.",
            new Subsurface { Radius = 0.01f }.Problem(clearcoat: 0.5f, 0));
    }

    [Fact]
    public void Foliage_is_a_problem_because_it_shares_the_channel()
    {
        Assert.Equal("subsurface can't be combined with foliage, because they share a G-buffer channel.",
            new Subsurface { Radius = 0.01f }.Problem(clearcoat: 0f, MaterialFlags.Foliage | MaterialFlags.TwoSided));
    }

    [Fact]
    public void Spread_scales_each_colour_by_the_radius_and_keeps_the_widest()
    {
        var spread = ResourceTable.Spread(new Subsurface { Radius = 0.01f, Color = new Vector3(1f, 0.4f, 0.25f) });

        AssertClose(new Vector4(0.01f, 0.004f, 0.0025f, 0.01f), spread);
    }

    [Fact]
    public void Spread_without_a_colour_reaches_the_radius_in_every_channel()
    {
        var spread = ResourceTable.Spread(new Subsurface { Radius = 0.02f });

        AssertClose(new Vector4(0.02f, 0.02f, 0.02f, 0.02f), spread);
    }

    [Fact]
    public void Spread_ignores_negative_colours_and_lets_one_pass_the_radius()
    {
        var spread = ResourceTable.Spread(new Subsurface { Radius = 0.01f, Color = new Vector3(0.5f, -1f, 2f) });

        AssertClose(new Vector4(0.005f, 0f, 0.02f, 0.02f), spread);
    }

    [Fact]
    public void Spread_of_an_off_subsurface_is_zero_whatever_its_colour()
    {
        Assert.Equal(Vector4.Zero, ResourceTable.Spread(new Subsurface { Color = new Vector3(1f, 0.4f, 0.25f), Transmission = 1f }));
    }

    [Fact]
    public void Transmission_and_spread_sit_where_material_data_glsl_reads_them()
    {
        // flags at 224, then transmission, then five height and detail fields; the vec4 rounds up to the next 16 bytes.
        Assert.Equal(228, (int)Marshal.OffsetOf<ResourceTable.GpuMaterial>(nameof(ResourceTable.GpuMaterial.Transmission)));
        Assert.Equal(256, (int)Marshal.OffsetOf<ResourceTable.GpuMaterial>(nameof(ResourceTable.GpuMaterial.Subsurface)));
    }

    private static void AssertClose(Vector4 expected, Vector4 actual)
    {
        Assert.True(Vector4.Distance(expected, actual) < 1e-6f, $"Expected {expected}, got {actual}.");
    }
}
