using System.Runtime.CompilerServices;
using Rendition;

namespace Super;

/// <summary>
/// The renderer's figures between two updates of <see cref="GameStats"/>: the GPU's pass times and the wait averaged
/// over the frames, and the newest frame's counts.
/// </summary>
internal sealed class RenderAverages
{
    /// <summary>How many of the slowest passes <see cref="RenderStats.SlowestPasses"/> lists.</summary>
    internal const int ListedPasses = 5;

    private readonly Dictionary<string, double> _passes = [];
    private double _gpu;
    private int _timedFrames;
    private double _wait;
    private int _renderedFrames;
    private RenderStats _newest;

    /// <summary>
    /// Adds a rendered frame: the GPU's pass times, which arrive a frame or two late and are empty when the GPU can't
    /// time them, the wait for the GPU and the display, and the frame's counts.
    /// </summary>
    public void Add(ReadOnlySpan<GpuPassTime> passes, double waitMilliseconds, in RenderStats counts)
    {
        if (!passes.IsEmpty)
        {
            foreach (ref readonly var pass in passes)
            {
                _passes[pass.Name] = _passes.GetValueOrDefault(pass.Name) + pass.Milliseconds;
                _gpu += pass.Milliseconds;
            }

            _timedFrames++;
        }

        _wait += waitMilliseconds;
        _renderedFrames++;
        _newest = counts;
    }

    /// <summary>The averages since the last take, which starts the next ones. Empty when nothing was rendered.</summary>
    public RenderStats Take()
    {
        if (_renderedFrames == 0)
        {
            return default;
        }

        int timed = Math.Max(_timedFrames, 1);
        var render = _newest with
        {
            GpuMilliseconds = _gpu / timed,
            WaitMilliseconds = _wait / _renderedFrames,
            SlowestPasses = Slowest(timed),
        };

        _passes.Clear();
        _gpu = 0.0;
        _timedFrames = 0;
        _wait = 0.0;
        _renderedFrames = 0;
        return render;
    }

    // Kept apart, and never inlined, because compiling a method with lambdas makes the class that caches them, which
    // allocates. In Take that would happen in a game that never renders, which must allocate nothing a frame.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private GpuPassTime[] Slowest(int timed) => _passes
        .Select(pass => new GpuPassTime(pass.Key, pass.Value / timed))
        .OrderByDescending(pass => pass.Milliseconds)
        .ThenBy(pass => pass.Name, StringComparer.Ordinal)
        .Take(ListedPasses)
        .ToArray();
}
