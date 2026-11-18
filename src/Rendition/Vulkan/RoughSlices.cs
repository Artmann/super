namespace Rendition.Vulkan;

/// <summary>
/// Which rough reflection levels a frame redraws. A change starts a round; each frame then takes the next slice,
/// turning on from wherever the last round stopped, so a sky that changes every frame still reaches every level.
/// </summary>
internal sealed class RoughSlices
{
    /// <summary>
    /// The first rough level costs about as much as the next three together, and the last, with the most samples,
    /// nearly as much again, so each slice takes about 0.2 to 0.3 ms on the GPU Hamlet was measured on.
    /// </summary>
    private static readonly int[][] Slices = [[1], [2, 3, 4], [5]];

    private int _left;
    private int _next;

    /// <summary>How many frames a change takes to reach every rough level.</summary>
    public static int Count => Slices.Length;

    /// <summary>Every rough level, in the order a full redraw draws them.</summary>
    public static IEnumerable<int> All => Slices.SelectMany(slice => slice);

    /// <summary>True while a level still shows an earlier sky than the last change.</summary>
    public bool Unfinished => _left > 0;

    /// <summary>Starts a round after a change: every slice is out of date.</summary>
    public void Restart() => _left = Slices.Length;

    /// <summary>Marks every slice drawn, after a full redraw.</summary>
    public void Clear() => _left = 0;

    /// <summary>The levels to draw this frame, marked drawn.</summary>
    public int[] Take()
    {
        var slice = Slices[_next];
        _next = (_next + 1) % Slices.Length;
        _left = Math.Max(_left - 1, 0);
        return slice;
    }
}
