using System.Numerics;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition.Gpu.Tests;

[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class FlatEnvironmentGpuTests(GpuFixture gpu)
{
    // No turbidity: the environment's bands, with no clear sky.
    private static readonly SkySun Bands = default;

    [Fact]
    public void Flat_colours_are_the_studio_colour_with_panels_only_in_the_reflection()
    {
        int errors = gpu.ValidationErrors;
        var colour = new Vector3(0.25f, 0.5f, 0.125f);
        using var environment = new EnvironmentMap(gpu.Context);

        // The same colour every way, so every face's middle, at any blur, is that colour. A panel overhead shows only in
        // the reflections: the irradiance leaves panels to the direct lights.
        environment.Bake(new EnvironmentDescription
        {
            Ceiling = colour,
            Horizon = colour,
            Floor = colour,
            Panels =
            [
                new() { Direction = Vector3.UnitY, HalfWidth = 0.5f, HalfHeight = 0.5f, Radiance = new Vector3(20f) },
            ],
        }, Bands);

        var (irradiance, reflection) = environment.Flat;

        AssertClose(colour, irradiance);
        Assert.True(reflection.Y > colour.Y + 0.05f, $"The panel should brighten the flat reflection, got {reflection}");
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Flat_colours_follow_each_bake()
    {
        var first = new Vector3(0.75f, 0.25f, 0.5f);
        var second = new Vector3(0.125f, 0.375f, 1f);
        using var environment = new EnvironmentMap(gpu.Context);

        environment.Bake(new EnvironmentDescription { Ceiling = first, Horizon = first, Floor = first }, Bands);
        var (firstIrradiance, firstReflection) = environment.Flat;
        environment.Bake(new EnvironmentDescription { Ceiling = second, Horizon = second, Floor = second }, Bands);
        var (secondIrradiance, secondReflection) = environment.Flat;

        AssertClose(first, firstIrradiance);
        AssertClose(first, firstReflection);
        AssertClose(second, secondIrradiance);
        AssertClose(second, secondReflection);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.True(Vector3.Distance(expected, actual) < 2e-3f, $"Expected {expected}, got {actual}");
    }
}
