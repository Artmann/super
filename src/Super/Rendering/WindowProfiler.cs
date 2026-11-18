using System.Diagnostics;
using System.Runtime.CompilerServices;
using Super.Profiling;

namespace Super;

/// <summary>What <c>--profile</c> asks for.</summary>
/// <param name="Title">The game's title, for the report.</param>
/// <param name="Seed">The game's seed, for the report.</param>
/// <param name="Ticks">The ticks played before measuring.</param>
/// <param name="Frames">The frames measured.</param>
/// <param name="Path">Where the report goes. The frames go beside it as CSV, and the CPU samples as a .nettrace.</param>
internal sealed record ProfileRequest(string Title, int Seed, long Ticks, int Frames, string Path);

/// <summary>What <c>--profile</c> does in the window: plays some frames, measures them and writes a report.</summary>
internal sealed class WindowProfiler(OsWindow window, SceneRenderer scene, WindowFrames frames)
{
    /// <summary>Frames played before measuring, so results don't include .NET still warming up.</summary>
    private const int ProfileWarmupFrames = 120;

    private const int ProfileFramesPerSecond = 60;

    /// <summary>Plays the asked ticks, then measures the asked frames and writes a report.</summary>
    public void Profile(GameLoop host, ProfileRequest request)
    {
        frames.PlayTo(host, request.Ticks);
        var clock = new FixedStepClock(host.Time.TicksPerSecond);
        double step = 1.0 / ProfileFramesPerSecond;
        string path = request.Path;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        scene.Renderer.MeasureGpu = true;
        var warmup = new ProfiledFrame[ProfileWarmupFrames];
        var gpu = new Dictionary<string, double>();
        MeasureFrames(host, clock, step, warmup, gpu);
        gpu.Clear();

        long firstTick = host.Time.Tick;
        var measured = new ProfiledFrame[request.Frames];
        var sampler = CpuSampler.Start(Path.ChangeExtension(path, ".nettrace"));
        MeasureFrames(host, clock, step, measured, gpu);
        var stacks = sampler.Stop(nameof(MeasureFrames));
        scene.Renderer.MeasureGpu = false;

        foreach (string pass in gpu.Keys)
        {
            gpu[pass] /= request.Frames;
        }

        var (width, height) = window.FramebufferSize();
        var run = new ProfileRun(request.Title, request.Seed, firstTick, width, height, measured, gpu, stacks,
            scene.CountModels(host.View, host.Content), scene.Renderer.DrawnTriangles);
        File.WriteAllText(path, ProfileReport.Write(run));
        File.WriteAllText(Path.ChangeExtension(path, ".csv"), ProfileReport.WriteFrames(measured));
    }

    /// <summary>The profiler keeps only stacks that pass through here, so it mustn't be inlined.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void MeasureFrames(GameLoop host, FixedStepClock clock, double step, ProfiledFrame[] measured, Dictionary<string, double> gpu)
    {
        float time = (float)host.Time.TotalSeconds;
        for (int frame = 0; frame < measured.Length; frame++)
        {
            int idleWaits = scene.Renderer.GpuIdleWaits;
            int captures = scene.Renderer.ReflectionCapturesDrawn;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            long start = Stopwatch.GetTimestamp();
            int due = clock.Advance(step);
            for (int i = 0; i < due; i++)
            {
                host.Tick();
            }

            long ticked = Stopwatch.GetTimestamp();
            frames.Draw(host, clock.Blend);
            long drawn = Stopwatch.GetTimestamp();
            frames.Show(host, time + (float)(frame * step));
            long shown = Stopwatch.GetTimestamp();

            double gpuTotal = 0.0;
            foreach (ref readonly var pass in scene.Renderer.GpuTimes)
            {
                gpu[pass.Name] = gpu.GetValueOrDefault(pass.Name) + pass.Milliseconds;
                gpuTotal += pass.Milliseconds;
            }

            measured[frame] = new ProfiledFrame(
                due,
                Stopwatch.GetElapsedTime(start, ticked).TotalMilliseconds,
                Stopwatch.GetElapsedTime(ticked, drawn).TotalMilliseconds,
                Stopwatch.GetElapsedTime(drawn, shown).TotalMilliseconds,
                scene.Renderer.WaitMilliseconds,
                GC.GetAllocatedBytesForCurrentThread() - allocated,
                gpuTotal,
                scene.Renderer.GpuIdleWaits - idleWaits,
                scene.Renderer.ReflectionCapturesDrawn - captures);
        }
    }
}
