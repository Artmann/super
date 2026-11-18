using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// What every step of a <see cref="BakeJob"/> works with: the baker, the area and the layout it bakes, the passes a
/// texel takes, the job's own descriptor pool and lights, the top-level structure it holds, and how much work is done.
/// </summary>
internal sealed unsafe class BakeJobKit
{
    private IDisposable? _sceneRelease;

    /// <summary>Makes the kit and counts the texel paths to trace. Makes nothing on the GPU until <see cref="Allocate"/>.</summary>
    public BakeJobKit(Baker baker, AreaJob area, BakeSettings settings, (ImageView View, Sampler Sampler) sky)
    {
        Baker = baker;
        Area = area;
        Layout = area.IsChunk ? area.Layout.OnFullPage() : area.Layout;
        Filter = settings.Filter;
        Sky = sky;
        Passes = settings.Quality == BakeQuality.Preview ? 64 : 1024;

        // The work is the texel paths and probe paths to trace; the rest is quick beside them.
        for (int page = 0; page < Layout.Pages; page++)
        {
            var (_, _, width, height) = Baker.UsedRegion(Layout, page);
            TotalWork += (double)width * height * Passes;
        }
    }

    public Baker Baker { get; }

    public VulkanContext Context => Baker.Context;

    public AreaJob Area { get; }

    /// <summary>The pages baked: the area's layout, drawn at the top left of a full page for a chunk.</summary>
    public LightmapLayout Layout { get; }

    /// <summary>Whether the page's light is smoothed, as <see cref="BakeSettings.Filter"/> says.</summary>
    public bool Filter { get; }

    /// <summary>The passes each texel's paths take.</summary>
    public int Passes { get; }

    public (ImageView View, Sampler Sampler) Sky { get; }

    /// <summary>The job's own descriptor pool, from <see cref="Allocate"/>.</summary>
    public DescriptorPool Pool { get; private set; }

    /// <summary>The bake's lights on the GPU, from <see cref="Allocate"/>.</summary>
    public GpuBuffer Lights { get; private set; } = null!;

    /// <summary>The top-level structure the job holds, from <see cref="SetUp"/>.</summary>
    public TopLevelStructure Scene { get; private set; }

    /// <summary>Whether <see cref="SetUp"/> has run, so the job holds the baker's pages.</summary>
    public bool Claimed { get; private set; }

    /// <summary>The paths to trace, texel and probe.</summary>
    public double TotalWork { get; set; }

    /// <summary>The paths traced so far.</summary>
    public double DoneWork { get; set; }

    /// <summary>The passes the last step traced, texel or probe, or 0 when it traced none.</summary>
    public int PassesInLastStep { get; set; }

    /// <summary>What the trace and probe sets bind: the held scene, the lights and the sky.</summary>
    public TraceInputs Inputs => new(Scene, Lights, Sky.View, Sky.Sampler);

    /// <summary>Uploads the lights and makes the job's descriptor pool.</summary>
    public void Allocate()
    {
        // An empty storage buffer isn't allowed, so there is always room for one light.
        var lights = Area.Lights;
        ulong lightBytes = (ulong)Math.Max(lights.Length, 1) * (ulong)sizeof(GpuBakeLight);
        Lights = GpuBuffer.CreateHostVisible(Context, lightBytes, BufferUsageFlags.StorageBufferBit);
        lights.CopyTo(new Span<GpuBakeLight>(Lights.Mapped, lights.Length));
        try
        {
            Pool = Baker.JobPools.Create(Layout.Pages);
        }
        catch
        {
            Lights.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Takes the baker's pages for <paramref name="owner"/>, builds the held top level and draws the texels into the
    /// pages.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another bake holds the baker's pages.</exception>
    public void SetUp(CommandBuffer cmd, object owner, string name)
    {
        Baker.Pages.Claim(owner, name);
        Claimed = true;
        Scene = Baker.Tracing.BuildHeldScene(cmd, Area.Draws, out var release);
        _sceneRelease = release;
        Baker.RecordTexels(cmd, Layout, Area.Draws, Pool, Scene);
    }

    /// <summary>Frees the lights, the pool and the held scene, and gives the baker's pages back.</summary>
    public void Free(object owner)
    {
        Lights.Dispose();
        Baker.JobPools.Destroy(Pool);
        _sceneRelease?.Dispose();
        _sceneRelease = null;
        if (Claimed)
        {
            Baker.Pages.Release(owner);
        }
    }
}

/// <summary>
/// Work a <see cref="BakeJob"/> runs on the thread pool, and what the step that finds it done records after it. The
/// work may touch only what it was handed and the job's mapped buffers, which the job keeps until it is done.
/// </summary>
internal sealed class BakeCpuWork
{
    private Task? _task;
    private Func<CommandBuffer, BakeStep>? _then;

    /// <summary>Whether the work is still running.</summary>
    public bool Busy => _task is { IsCompleted: false };

    /// <summary>
    /// Starts <paramref name="work"/> on the thread pool; the first step that finds it done calls
    /// <paramref name="then"/> on its own thread to record what follows.
    /// </summary>
    public BakeStep Start(Action work, Func<CommandBuffer, BakeStep> then)
    {
        _task = Task.Run(work);
        _then = then;
        return new BakeStep(BakeStepResult.AwaitCpu, BakeStage.Cpu);
    }

    /// <summary>Records what follows the work once it is done, or waits on.</summary>
    public BakeStep After(CommandBuffer cmd)
    {
        var task = _task!;
        if (!task.IsCompleted)
        {
            return new BakeStep(BakeStepResult.AwaitCpu, BakeStage.Cpu);
        }

        var then = _then!;
        _task = null;
        _then = null;

        // Throws the work's own exception, as if it had run in this step.
        task.GetAwaiter().GetResult();
        return then(cmd);
    }

    /// <summary>Waits for the work. Its error, if any, comes out of <see cref="After"/>.</summary>
    public void Wait()
    {
        if (_task is not { } task)
        {
            return;
        }

        try
        {
            task.Wait();
        }
        catch (AggregateException)
        {
            // Thrown again from the next step, on the thread that runs it.
        }
    }

    /// <summary>Drops the work and what follows it, once it is done.</summary>
    public void Forget()
    {
        _task = null;
        _then = null;
    }
}
