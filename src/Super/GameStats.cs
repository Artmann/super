using System.Diagnostics;

namespace Super;

/// <summary>A summary of the last moments of play.</summary>
/// <param name="FramesPerSecond">Frames shown per second.</param>
/// <param name="FrameMilliseconds">How long an average frame took.</param>
/// <param name="AllocatedBytesPerFrame">Memory allocated on the game's thread in an average frame.</param>
/// <param name="HeapBytes">The size of the garbage collected heap.</param>
/// <param name="Collections">Garbage collections since the game started.</param>
public readonly record struct FrameStats(
    double FramesPerSecond,
    double FrameMilliseconds,
    long AllocatedBytesPerFrame,
    long HeapBytes,
    int Collections);

/// <summary>
/// How the game is running, for showing on screen. It updates twice a second, slowly
/// enough to read. These numbers come from the real clock and change from run to run,
/// so they must never reach Update: that would stop a seed from repeating.
/// </summary>
public sealed class GameStats
{
    private const double SecondsBetweenUpdates = 0.5;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _measuringFrom = GC.GetAllocatedBytesForCurrentThread();
    private long _allocated;
    private int _frames;

    public FrameStats Latest { get; private set; }

    /// <summary>Ends a frame's measurement. What happens before <see cref="Resume"/> isn't counted.</summary>
    internal void EndFrame()
    {
        _allocated += GC.GetAllocatedBytesForCurrentThread() - _measuringFrom;
        _frames++;

        double seconds = _clock.Elapsed.TotalSeconds;
        if (seconds < SecondsBetweenUpdates)
        {
            return;
        }

        Latest = new FrameStats(
            _frames / seconds,
            seconds * 1000.0 / _frames,
            _allocated / _frames,
            GC.GetTotalMemory(forceFullCollection: false),
            GC.CollectionCount(0));
        _allocated = 0;
        _frames = 0;
        _clock.Restart();
    }

    internal void Resume() => _measuringFrom = GC.GetAllocatedBytesForCurrentThread();
}
