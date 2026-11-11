using System.Numerics;

namespace Rendition.Gpu.Tests;

[Collection(GpuCollection.Name)]
[Trait("Speed", "Slow")]
[Trait("Needs", "GPU")]
public sealed class EnvironmentHashGpuTests(GpuFixture gpu)
{
    // What the renderer starts with, put back after each test so the others see the usual environment.
    private static EnvironmentDescription Default() => new()
    {
        Ceiling = new Vector3(0.5f),
        Horizon = new Vector3(0.4f),
        Floor = new Vector3(0.2f),
    };

    private static readonly EnvironmentPanel[] Softbox =
    [
        new() { Direction = Vector3.UnitY, HalfWidth = 0.3f, HalfHeight = 0.2f, Radiance = new Vector3(8f) },
    ];

    [Fact]
    public void Environment_hash_follows_its_content_not_its_version()
    {
        int errors = gpu.ValidationErrors;
        try
        {
            gpu.Renderer.SetEnvironment(Studio(new Vector3(8f)));
            ulong first = gpu.Renderer.EnvironmentHash;

            gpu.Renderer.SetEnvironment(Studio(new Vector3(9f)));
            ulong other = gpu.Renderer.EnvironmentHash;

            // Back to the first: the renderer's version counter has moved twice since the first reading.
            gpu.Renderer.SetEnvironment(Studio(new Vector3(8f)));
            ulong again = gpu.Renderer.EnvironmentHash;

            Assert.NotEqual(first, other);
            Assert.Equal(first, again);
        }
        finally
        {
            gpu.Renderer.SetEnvironment(Default());
        }

        Assert.Equal(errors, gpu.ValidationErrors);
    }

    [Fact]
    public void Environment_hash_follows_the_bands()
    {
        try
        {
            gpu.Renderer.SetEnvironment(Default());
            ulong before = gpu.Renderer.EnvironmentHash;
            gpu.Renderer.SetEnvironment(Default() with { Floor = new Vector3(0.3f) });

            Assert.NotEqual(before, gpu.Renderer.EnvironmentHash);
        }
        finally
        {
            gpu.Renderer.SetEnvironment(Default());
        }
    }

    [Fact]
    public void Environment_hash_follows_the_sky_and_environment_intensity_of_the_last_frame()
    {
        int errors = gpu.ValidationErrors;
        gpu.RenderOneFrame(new AnalyticSky(), 1f);
        ulong plain = gpu.Renderer.EnvironmentHash;

        gpu.RenderOneFrame(new AnalyticSky { Turbidity = 3f }, 1f);
        ulong hazy = gpu.Renderer.EnvironmentHash;

        gpu.RenderOneFrame(new AnalyticSky(), 2f);
        ulong brighter = gpu.Renderer.EnvironmentHash;

        gpu.RenderOneFrame(new AnalyticSky(), 1f);
        ulong plainAgain = gpu.Renderer.EnvironmentHash;

        gpu.RenderOneFrame();

        Assert.NotEqual(plain, hazy);
        Assert.NotEqual(plain, brighter);
        Assert.Equal(plain, plainAgain);
        Assert.Equal(errors, gpu.ValidationErrors);
    }

    private static EnvironmentDescription Studio(Vector3 panelRadiance)
    {
        var panels = new EnvironmentPanel[Softbox.Length];
        Softbox.CopyTo(panels, 0);
        panels[0].Radiance = panelRadiance;
        return Default() with { Panels = panels };
    }
}
