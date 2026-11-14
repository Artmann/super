using System.Runtime.InteropServices;
using Super.Testing;
using Xunit;

namespace Spinner.Tests;

public sealed class SpinnerTests
{
    [Fact]
    public void The_cube_turns_as_fast_as_its_content_file_says()
    {
        using var run = HeadlessGame.Create<SpinnerGame>(seed: 1);

        // 30 ticks are one second, and red.json says a quarter turn a second.
        run.Ticks(30);

        Assert.Equal(0.25f, run.Game.Cube.TurnsPerSecond);
        Assert.Equal(MathF.PI / 2f, run.Game.Angle, 3);
        Assert.Contains(run.Drawn, draw => draw.Key == 1);
    }

    [Fact]
    public void The_upscaler_library_is_found_the_way_the_renderer_looks_for_it()
    {
        bool found = NativeLibrary.TryLoad(
            "amd_fidelityfx_vk",
            typeof(Rendition.Renderer).Assembly,
            DllImportSearchPath.AssemblyDirectory,
            out _);

        Assert.True(found, "amd_fidelityfx_vk.dll wasn't found, so FSR upscaling would quietly fall back to TAA.");
    }
}
