using System.Globalization;
using System.Runtime.CompilerServices;
using Super.Profiling;

namespace Super.Tests;

public sealed class ProfilingTests
{
    private static ProfileRun Run(IReadOnlyList<string[]> stacks, Dictionary<string, double>? gpu = null, double waiting = 0.0) => new(
        "Test",
        7,
        600,
        1280,
        720,
        [new ProfiledFrame(1, 2.0, 3.0, 5.0, waiting, 128), new ProfiledFrame(0, 2.0, 3.0, 5.0, waiting, 128)],
        gpu ?? new Dictionary<string, double> { ["Lighting"] = 3.0, ["Shadows"] = 1.0 },
        stacks);

    /// <summary>Checks that both appear, which a plain IndexOf comparison would not: a missing one is -1.</summary>
    private static void AssertInOrder(string report, string first, string second)
    {
        int before = report.IndexOf(first, StringComparison.Ordinal);
        int after = report.IndexOf(second, StringComparison.Ordinal);
        Assert.True(before >= 0, $"Not found: {first}");
        Assert.True(after >= 0, $"Not found: {second}");
        Assert.True(before < after, $"{first} should come before {second}");
    }

    [Fact]
    public void Functions_are_ranked_by_time_in_themselves()
    {
        string[][] stacks = [["Main", "Update", "Slow"], ["Main", "Update", "Slow"], ["Main", "Draw"]];

        var functions = ProfileReport.Functions(stacks);

        Assert.Equal(("Slow", 2, 2), functions[0]);
        Assert.Equal(("Draw", 1, 1), functions[1]);
        Assert.Contains(("Main", 0, 3), functions);
    }

    [Fact]
    public void Recursion_counts_once_towards_a_functions_total()
    {
        var functions = ProfileReport.Functions([["Walk", "Walk", "Walk"]]);

        Assert.Equal(("Walk", 1, 1), Assert.Single(functions));
    }

    [Fact]
    public void The_report_gives_the_frame_the_passes_and_the_stacks()
    {
        string report = ProfileReport.Write(Run([["Main", "Slow"]]));

        Assert.Contains("seed 7", report);
        Assert.Contains("10.0 ms on average", report);
        AssertInOrder(report, "Lighting", "Shadows"); // Biggest first.
        Assert.Contains("100.0% of samples:\n  Main\n  Slow\n", report);
    }

    [Fact]
    public void The_report_says_which_side_is_holding_things_up()
    {
        Assert.Contains("Limited by the GPU", ProfileReport.Write(Run([], waiting: 8.0)));
        Assert.Contains("Limited by the CPU", ProfileReport.Write(Run([], waiting: 0.5)));
    }

    [Fact]
    public void Waiting_for_the_display_does_not_blame_the_gpu()
    {
        // The CPU works for 2 ms, the GPU for 1 ms, and the other 7 ms is waiting for the display.
        var gpu = new Dictionary<string, double> { ["Lighting"] = 1.0 };

        Assert.Contains("Limited by the CPU", ProfileReport.Write(Run([], gpu, waiting: 8.0)));
    }

    [Fact]
    public void The_report_says_when_there_are_no_gpu_times()
    {
        Assert.Contains("can't time its passes", ProfileReport.Write(Run([], new Dictionary<string, double>())));
    }

    [Fact]
    public void The_report_ranks_models_by_their_triangles()
    {
        ModelCost[] models = [new("small", 100, 10), new("big", 2, 1000)];

        string report = ProfileReport.Write(Run([]) with { Models = models, DrawnTriangles = (1234, 56) });

        Assert.Contains("102 models, 3,000 triangles", report);
        Assert.Contains("1,234 triangles for the camera, 56 for shadows", report);
        AssertInOrder(report, "big", "small");
    }

    [Fact]
    public void The_report_counts_light_shadow_tiles_drawn_and_kept_only_when_there_are_any()
    {
        ModelCost[] models = [new("small", 100, 10)];

        string withTiles = ProfileReport.Write(Run([]) with { Models = models, ShadowTiles = (3, 1200) });
        string without = ProfileReport.Write(Run([]) with { Models = models });

        Assert.Contains("Light shadow tiles: 3 drawn, 1,200 kept from earlier frames", withTiles);
        Assert.DoesNotContain("Light shadow tiles", without);
    }

    [Fact]
    public void The_report_reads_the_same_in_a_culture_with_decimal_commas()
    {
        ModelCost[] models = [new("small", 100, 10), new("big", 2, 1000)];
        var run = Run([["Main", "Slow"]]) with { Models = models, DrawnTriangles = (1234, 56) };
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");
            Assert.Equal("1,5", 1.5.ToString(CultureInfo.CurrentCulture));

            string report = ProfileReport.Write(run);

            Assert.Contains("10.0 ms on average", report);
            Assert.Contains("3,000 triangles", report);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void The_report_lists_the_slowest_frames_first_with_their_idle_waits_and_captures()
    {
        ProfiledFrame[] frames =
        [
            new(1, 2.0, 3.0, 5.0, 0.0, 128, Gpu: 4.0),
            new(1, 2.0, 3.0, 35.0, 0.0, 128, Gpu: 4.5, IdleWaits: 2, ReflectionCaptures: 7),
            new(1, 2.0, 3.0, 7.0, 0.0, 128, Gpu: 4.0, ReflectionCaptures: 1),
        ];
        var run = Run([["Main"]]) with { Frames = frames };

        string report = ProfileReport.Write(run);

        Assert.Contains("Stopped until the GPU was idle 2 times in 3 frames. Reflection probe captures: 8.", report);
        // 2 + 3 + 35 ms, then 2 + 3 + 7 ms, then 2 + 3 + 5 ms.
        AssertInOrder(report, "frame    1     40.0 ms  GPU    4.5 ms  2 idle waits  7 reflection captures",
            "frame    2     12.0 ms  GPU    4.0 ms  0 idle waits  1 reflection captures");
        AssertInOrder(report, "frame    2     12.0 ms", "frame    0     10.0 ms");
    }

    [Fact]
    public void The_frames_file_has_a_line_for_each_frame_with_points_for_decimals_in_any_culture()
    {
        ProfiledFrame[] frames = [new(2, 1.5, 0.25, 6.0, 4.0, 64, Gpu: 3.125, IdleWaits: 1, ReflectionCaptures: 1)];
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("sv-SE");

            string csv = ProfileReport.WriteFrames(frames);

            Assert.Equal(
                "frame,ticks,frame_ms,update_ms,draw_ms,render_ms,waiting_ms,gpu_ms,idle_waits,reflection_captures\n"
                + "0,2,7.750,1.500,0.250,6.000,4.000,3.125,1,1\n",
                csv);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }
}

/// <summary>The sampler listens to the whole process, so nothing else may run beside it.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CpuSamplerCollection
{
    public const string Name = "CPU sampler";
}

[Collection(CpuSamplerCollection.Name)]
[Trait("Speed", "Slow")]
public sealed class CpuSamplerTests
{
    [Fact]
    public void The_sampler_finds_a_busy_function_by_name()
    {
        string folder = Directory.CreateTempSubdirectory("super-profile").FullName;
        try
        {
            var sampler = CpuSampler.Start(Path.Combine(folder, "run.nettrace"));
            Measured();
            var stacks = sampler.Stop(nameof(Measured));

            Assert.NotEmpty(stacks);
            Assert.Contains(stacks, stack => stack.Any(name => name.Contains(nameof(BusyWork))));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Measured() => BusyWork();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static double BusyWork()
    {
        double sum = 0.0;
        var until = DateTime.UtcNow.AddMilliseconds(300);
        while (DateTime.UtcNow < until)
        {
            sum += Math.Sqrt(sum + 1.0);
        }

        return sum;
    }
}
