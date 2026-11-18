using Rendition;

namespace Super;

/// <summary>A summary of the last moments of play.</summary>
/// <param name="FramesPerSecond">Frames shown per second.</param>
/// <param name="FrameMilliseconds">How long an average frame took.</param>
/// <param name="AllocatedBytesPerFrame">Memory allocated on the game's thread in an average frame.</param>
/// <param name="HeapBytes">The size of the garbage collected heap.</param>
/// <param name="Collections">Garbage collections since the game started.</param>
/// <param name="Render">
/// What the renderer did, while <see cref="GameStats.Shown"/> is on in a window. Empty otherwise, because measuring the
/// GPU costs a little.
/// </param>
public readonly record struct FrameStats(
    double FramesPerSecond,
    double FrameMilliseconds,
    long AllocatedBytesPerFrame,
    long HeapBytes,
    int Collections,
    RenderStats Render = default)
{
    /// <summary>
    /// How long an average frame kept the CPU busy: the frame's time less the renderer's wait for the GPU and the
    /// display. Only known while the renderer is measured; otherwise it is the whole frame.
    /// </summary>
    public double BusyMilliseconds => Math.Max(FrameMilliseconds - Render.WaitMilliseconds, 0.0);
}

/// <summary>What the renderer did in the last moments of play.</summary>
/// <param name="GpuMilliseconds">How long an average frame kept the GPU busy. Zero when the GPU can't time its passes.</param>
/// <param name="WaitMilliseconds">How long an average frame waited for the GPU and the display.</param>
/// <param name="SlowestPasses">The passes that took the GPU longest, slowest first, on average. Null when nothing was measured.</param>
/// <param name="DrawCalls">Model draw calls in the newest frame, over every pass.</param>
/// <param name="CameraTriangles">Triangles the camera drew in the newest frame, after culling and detail.</param>
/// <param name="ShadowTriangles">Triangles the shadows drew in the newest frame. A still light's kept shadow isn't counted.</param>
/// <param name="ShadowTilesDrawn">Point and spot light shadow tiles drawn in the newest frame.</param>
/// <param name="ShadowTilesKept">Point and spot light shadow tiles kept from earlier frames in the newest frame.</param>
/// <param name="GpuMemoryUsedBytes">GPU memory the renderer uses.</param>
/// <param name="GpuMemoryReservedBytes">GPU memory the renderer has taken from the device, free room in its blocks included.</param>
/// <param name="Width">The width of the frame in pixels.</param>
/// <param name="Height">The height of the frame in pixels.</param>
public readonly record struct RenderStats(
    double GpuMilliseconds,
    double WaitMilliseconds,
    IReadOnlyList<GpuPassTime>? SlowestPasses,
    int DrawCalls,
    long CameraTriangles,
    long ShadowTriangles,
    int ShadowTilesDrawn,
    int ShadowTilesKept,
    long GpuMemoryUsedBytes,
    long GpuMemoryReservedBytes,
    int Width,
    int Height)
{
    /// <summary>Whether the renderer was measured at all.</summary>
    public bool Measured => SlowestPasses is not null;
}

/// <summary>
/// How the game is running, for showing on screen. It updates twice a second, slowly
/// enough to read. These numbers come from the real clock and change from run to run,
/// so they must never reach Update: that would stop a seed from repeating.
/// </summary>
public sealed class GameStats
{
    private const double SecondsBetweenUpdates = 0.5;

    private readonly TimeProvider _time;
    private readonly RenderAverages _render = new();
    private long _since;
    private long _measuringFrom = GC.GetAllocatedBytesForCurrentThread();
    private long _allocated;
    private int _frames;

    public GameStats()
        : this(TimeProvider.System)
    {
    }

    internal GameStats(TimeProvider time)
    {
        _time = time;
        _since = time.GetTimestamp();
    }

    public FrameStats Latest { get; private set; }

    /// <summary>
    /// Whether the engine shows its stats panel over the game, with the renderer's figures in it.
    /// <see cref="ToggleKey"/> turns it on and off in a window, and <c>--stats</c> starts with it on. A game may turn
    /// it on from a menu of its own.
    /// </summary>
    public bool Shown { get; set; }

    /// <summary>
    /// The key that shows and hides the stats panel in a window, F3 unless the game picks another. The game never
    /// sees this key. Null turns the key off, for a shipped game, and then the game gets every key; <see cref="Shown"/>
    /// and <c>--stats</c> still work.
    /// </summary>
    public Key? ToggleKey { get; set; } = Key.F3;

    /// <summary>
    /// Takes a key from the window before the game sees it: the toggle key flips <see cref="Shown"/> when pressed.
    /// Returns whether the key was the toggle key, which the game then never hears of.
    /// </summary>
    internal bool TakeKey(Key key, bool pressed)
    {
        if (key != ToggleKey)
        {
            return false;
        }

        if (pressed)
        {
            Shown = !Shown;
        }

        return true;
    }

    /// <summary>Ends a frame's measurement. What happens before <see cref="Resume"/> isn't counted.</summary>
    internal void EndFrame()
    {
        _allocated += GC.GetAllocatedBytesForCurrentThread() - _measuringFrom;
        _frames++;

        double seconds = _time.GetElapsedTime(_since).TotalSeconds;
        if (seconds < SecondsBetweenUpdates)
        {
            return;
        }

        Latest = new FrameStats(
            _frames / seconds,
            seconds * 1000.0 / _frames,
            _allocated / _frames,
            GC.GetTotalMemory(forceFullCollection: false),
            GC.CollectionCount(0),
            _render.Take());
        _allocated = 0;
        _frames = 0;
        _since = _time.GetTimestamp();
    }

    internal void Resume() => _measuringFrom = GC.GetAllocatedBytesForCurrentThread();

    /// <summary>Forgets everything measured so far, so the next update covers only the frames after this.</summary>
    internal void Restart()
    {
        Latest = default;
        _allocated = 0;
        _frames = 0;
        _since = _time.GetTimestamp();
        _render.Take();
    }

    /// <summary>
    /// Adds a rendered frame: the GPU's pass times, which arrive a frame or two late and are empty when the GPU can't
    /// time them, the wait for the GPU and the display, and the frame's counts. The times are averaged over the
    /// moments between updates; the counts are the newest frame's.
    /// </summary>
    internal void AddRender(ReadOnlySpan<GpuPassTime> passes, double waitMilliseconds, in RenderStats counts) =>
        _render.Add(passes, waitMilliseconds, counts);
}
