namespace Rendition;

/// <summary>What the renderer draws into, how large, and how it starts out.</summary>
public sealed class RendererOptions
{
    /// <summary>The window drawn into, or <see cref="RendererSurface.Headless"/>.</summary>
    public required RendererSurface Surface { get; init; }

    public required int Width { get; init; }
    public required int Height { get; init; }
    public bool EnableValidation { get; init; }

    /// <summary>
    /// Draws as fast as the machine can instead of waiting for the display. Frames may
    /// tear. Displays that can't do this keep vsync on.
    /// </summary>
    public bool DisableVsync { get; init; }

    /// <summary>
    /// Makes the device without ray queries even where the GPU has them, so <see cref="GpuInfo.CanTrace"/> is false.
    /// For seeing what a player whose GPU can't trace gets.
    /// </summary>
    public bool DisableRayTracing { get; init; }

    /// <summary>How many texels across each of the sun's cascade maps starts: 1024, 2048 or 4096. See <see cref="RenderSettings.ShadowResolution"/>.</summary>
    public int ShadowResolution { get; init; } = 2048;

    /// <summary>How large the scene starts out drawn against the window, from 0.5 to 2. See <see cref="RenderSettings.RenderScale"/>.</summary>
    public float RenderScale { get; init; } = 1f;

    /// <summary>What anti-aliases the scene and, below a render scale of 1, upscales it. See <see cref="RenderSettings.Upscaler"/>.</summary>
    public Upscaler Upscaler { get; init; } = Upscaler.Taa;

    /// <summary>
    /// Keeps how each pipeline was made, so <see cref="RendererAssets.ReloadShaders"/> can build the ones that use a
    /// changed shader again while the game runs. For development; it costs a little memory per pipeline.
    /// </summary>
    public bool LiveShaders { get; init; }
}

/// <summary>How many light probes follow the camera, and so how much ground they cover and how much they cost.</summary>
public enum ProbeQuality
{
    /// <summary>Four cascades of 16 × 8 × 16 probes.</summary>
    Low,

    /// <summary>Four cascades of 24 × 12 × 24 probes.</summary>
    Medium,

    /// <summary>Four cascades of 32 × 16 × 32 probes.</summary>
    High
}

/// <summary>How many of the screen's pixels trace their reflection each frame.</summary>
public enum ReflectionResolution
{
    /// <summary>
    /// One pixel in four traces a reflection, a different one each frame, and the pixels between take theirs from the
    /// traced ones round them on the same surface. Anti-aliasing blends the frames into a full picture.
    /// </summary>
    Half,

    /// <summary>Every pixel traces its own reflection every frame.</summary>
    Full
}

/// <summary>How many of the screen's pixels work out their ambient occlusion each frame.</summary>
public enum OcclusionResolution
{
    /// <summary>
    /// One pixel in four works out its occlusion, a different one each frame, and the pixels between take theirs from
    /// the ones round them at the same depth. Anti-aliasing blends the frames into a full picture.
    /// </summary>
    Half,

    /// <summary>Every pixel works out its own occlusion every frame.</summary>
    Full
}

/// <summary>How long one render pass took on the GPU.</summary>
public readonly record struct GpuPassTime(string Name, double Milliseconds);

/// <summary>
/// How many times the GPU ran each kind of shader in one render pass. Unlike the time, it is exact: a pass whose work
/// is skipped runs none, and one that still runs counts every shader, even those that leave at once.
/// </summary>
public readonly record struct GpuPassWork(string Name, ulong VertexShaders, ulong FragmentShaders, ulong ComputeShaders)
{
    /// <summary>Every shader the pass ran, of any kind.</summary>
    public ulong Shaders => VertexShaders + FragmentShaders + ComputeShaders;
}

/// <summary>What kind of GPU draws the game, as the driver says.</summary>
public enum GpuKind
{
    /// <summary>Not known yet, or a kind the driver didn't name.</summary>
    Unknown,

    /// <summary>A graphics card of its own, with its own memory: the fastest kind.</summary>
    Discrete,

    /// <summary>Built into the processor, sharing the computer's memory, as in most laptops.</summary>
    Integrated,

    /// <summary>A virtual machine's GPU, or the processor drawing in software: the slowest kind.</summary>
    Virtual,
}

/// <summary>The GPU the renderer draws with.</summary>
/// <param name="Name">The name the driver gives it, such as "NVIDIA GeForce RTX 4060 Ti".</param>
/// <param name="Kind">Whether it's a card of its own, built into the processor or virtual.</param>
/// <param name="MemoryBytes">
/// The size of its largest memory heap of its own. On an integrated GPU this is a share of the computer's memory.
/// </param>
/// <param name="CanTrace">
/// Whether it traces rays, with ray queries turned on for the device. Nothing requires it: a GPU that can't, or
/// <see cref="RendererOptions.DisableRayTracing"/>, just draws without tracing. <see cref="Renderer.NoTraceReason"/>
/// says why not.
/// </param>
public readonly record struct GpuInfo(string Name, GpuKind Kind, long MemoryBytes, bool CanTrace);
