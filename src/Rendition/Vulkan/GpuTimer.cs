using Rendition.Gpu;
using Rendition.Gpu.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Times each render pass on the GPU. The GPU writes a timestamp after each pass, through the GPU layer, and the
/// results are read once that frame has finished, a frame or two later. Marks must come in the order of the timer's
/// pass names. Given a <see cref="GpuWorkCounter"/>, it also has the shaders each pass runs counted at the same marks.
/// </summary>
internal sealed class GpuTimer : IDisposable
{
    private const double NanosecondsPerMillisecond = 1_000_000.0;

    /// <summary>The passes of a frame, in the order the renderer marks them.</summary>
    internal static readonly string[] FramePasses =
    [
        "Sky", "Grass scatter", "Shadows", "Grass shadows", "Light probes", "Reflection probes", "Planar reflection",
        "Light clusters", "Clouds", "Volumetric fog", "G-buffer", "Grass blades", "Decals", "Ambient occlusion", "AO blur",
        "Reflections", "Lighting", "Subsurface", "Glass and particles",
        "Anti-aliasing", "Depth of field", "Bloom", "Paint", "Tonemap", "Finish", "Overlay",
    ];

    private readonly string[] _passNames;
    private readonly VulkanGpu _gpu;
    // One query set per frame slot, and how many marks that slot got since it was last read. A frame that left a
    // mark out isn't read, since that query still holds an older frame's time.
    private readonly (QuerySetId[] Sets, int[] Marks) _slots;
    private readonly ulong[] _stamps;
    private readonly GpuPassTime[] _times;
    private readonly GpuWorkCounter? _counter;
    // Each mark is a list of its own, played between the passes of the frame's command buffer.
    private readonly CommandList _mark = new();
    private int _nextMark;
    // The frame slot being timed, or -1 outside a timed frame.
    private int _frame = -1;
    private bool _collected;

    /// <summary>
    /// Times the frame's passes, or <paramref name="passNames"/> of its own, such as the slice of a background bake.
    /// A <paramref name="counter"/> made for the same passes counts their shaders too, and is disposed with the timer.
    /// </summary>
    public GpuTimer(VulkanGpu gpu, int framesInFlight, string[]? passNames = null, GpuWorkCounter? counter = null)
    {
        passNames ??= FramePasses;
        _passNames = passNames;
        _gpu = gpu;
        _counter = counter;
        _stamps = new ulong[passNames.Length + 1];
        _times = new GpuPassTime[passNames.Length];
        _slots = (new QuerySetId[framesInFlight], new int[framesInFlight]);
        for (int i = 0; i < framesInFlight; i++)
        {
            _slots.Sets[i] = gpu.CreateQuerySet(_stamps.Length);
        }
    }

    /// <summary>Whether the GPU can time passes at all. Without it, <see cref="Begin"/> times nothing.</summary>
    public bool IsSupported => _slots.Sets[0].Value != 0;

    /// <summary>The newest finished frame's passes. Empty until one has finished.</summary>
    public ReadOnlySpan<GpuPassTime> Times => _collected ? _times : default;

    public void Begin(CommandBuffer cmd, int frame)
    {
        if (!IsSupported)
        {
            return;
        }

        _frame = frame;
        _counter?.Begin(cmd, frame);
        _nextMark = 0;
        Mark(cmd);
    }

    /// <summary>
    /// Marks the end of a pass. Does nothing outside a timed frame, as when capturing a probe. Marks go between render
    /// passes, as WebGPU writes timestamps only there and a count can't begin or end inside one.
    /// </summary>
    public void Mark(CommandBuffer cmd)
    {
        if (_frame >= 0)
        {
            _counter?.Mark(cmd, (uint)_nextMark);
            _mark.Reset();
            _mark.WriteTimestamp(_slots.Sets[_frame], _nextMark++);
            _gpu.Play(_mark, cmd);
            _slots.Marks[_frame] = _nextMark;
        }
    }

    /// <summary>Ends the timed frame, and the count of a pass left open.</summary>
    public void End(CommandBuffer cmd)
    {
        if (_frame >= 0)
        {
            _counter?.End(cmd, (uint)_nextMark);
        }

        _frame = -1;
    }

    /// <summary>
    /// Reads a frame's results, once. Its fence must have been waited on. False when that slot wasn't timed since it
    /// was last read, or its frame left a mark out.
    /// </summary>
    public bool Collect(int frame)
    {
        bool whole = _slots.Marks[frame] == _stamps.Length;
        _slots.Marks[frame] = 0;
        if (!whole || !_gpu.ReadQuerySet(_slots.Sets[frame], _stamps))
        {
            return false;
        }

        for (int i = 0; i < _times.Length; i++)
        {
            double nanoseconds = _stamps[i + 1] - _stamps[i];
            _times[i] = new GpuPassTime(_passNames[i], nanoseconds / NanosecondsPerMillisecond);
        }

        _collected = true;
        _counter?.Collect(frame);
        return true;
    }

    public void Dispose()
    {
        foreach (var set in _slots.Sets.Where(set => set.Value != 0))
        {
            _gpu.Destroy(set);
        }

        _counter?.Dispose();
    }
}
