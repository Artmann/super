using System.Numerics;
using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The reflection probes, each with its cube in <see cref="ProbeCubes"/> and one mip per roughness step. A probe added or
/// updated waits in line for its capture, and the renderer takes one a frame from the front with <see cref="TakeNext"/>,
/// captures it with <see cref="ReflectionCapturePass"/> and blurs the capture into the probe's cube with
/// <see cref="RecordFilter"/>, all inside the frame. A probe shows from the frame of its first capture, and until a new one
/// lands it keeps its last. A removed probe's cube goes to the next one added, and when none is free the array grows.
/// Each frame <see cref="Prepare"/> picks and orders the captured probes the view sees.
/// </summary>
internal sealed unsafe class ReflectionProbes : IDisposable
{
    private const int MaxProbes = ReflectionProbeOrder.MaxProbes;
    private const int Mips = ProbeCubes.Mips;

    private readonly VulkanContext _context;
    private readonly ReflectionFilter _filter;

    // What each captured probe showed at its last capture, or null for a cube nobody has captured into yet.
    private readonly ReflectionProbe?[] _slots = new ReflectionProbe?[MaxProbes];

    // Which cubes belong to a probe, captured or still waiting.
    private readonly bool[] _held = new bool[MaxProbes];
    private readonly ReflectionCaptureLine _line = new();

    /// <summary>The array every probe's cube is in.</summary>
    public ProbeCubes Cubes { get; }

    /// <summary>The probes the last <see cref="Prepare"/> found in view, in the order they blend.</summary>
    public ReflectionProbeOrder Order { get; } = new();

    /// <summary>How many probes there are, captured or waiting for their first capture.</summary>
    public int Count { get; private set; }

    public ReflectionProbes(VulkanContext context, Sampler sampler, int framesInFlight)
    {
        _context = context;
        Cubes = new ProbeCubes(context);
        _filter = new ReflectionFilter(context, sampler, framesInFlight, Mips, ProbeCubes.FaceSize);
    }

    /// <summary>
    /// Writes the captured probes the view sees to <paramref name="gpu"/> in the order they blend, and says how many.
    /// <see cref="Order"/> keeps the depths their boxes span.
    /// </summary>
    public int Prepare(in Matrix4x4 view, in Matrix4x4 projection, Span<GpuReflectionProbe> gpu)
    {
        Order.Prepare(_slots.AsSpan(0, Cubes.Capacity), view, projection, gpu);
        return Order.Count;
    }

    /// <summary>
    /// Takes a cube for a new probe, the first free one, and puts the probe at the back of the line for a capture. When
    /// none is free the array grows first, which waits for the GPU.
    /// </summary>
    public ReflectionProbeHandle Add(in ReflectionProbe probe)
    {
        if (Count == MaxProbes)
        {
            throw new InvalidOperationException($"Only {MaxProbes} reflection probes are supported.");
        }

        int slot = Array.IndexOf(_held, false, 0, Cubes.Capacity);
        if (slot < 0)
        {
            slot = Cubes.Capacity;
            Cubes.Grow();
        }

        _held[slot] = true;
        Count++;
        _line.Ask(slot, probe);
        return new ReflectionProbeHandle(slot);
    }

    /// <summary>
    /// Asks for a new capture of a probe, placed as <paramref name="probe"/> says. A probe already waiting keeps its
    /// place in line and takes the new placement; any other goes to the back.
    /// </summary>
    public void Update(ReflectionProbeHandle handle, in ReflectionProbe probe) => _line.Ask(Slot(handle, required: true), probe);

    /// <summary>Whether a probe is in line for a capture that hasn't been drawn yet.</summary>
    public bool Waiting(ReflectionProbeHandle handle) => _line.Holds(Slot(handle, required: true));

    /// <summary>
    /// Takes the probe at the front of the line, for this frame's capture, or returns false when none waits. Call
    /// <see cref="RecordFilter"/> for it in the same frame.
    /// </summary>
    public bool TakeNext(out int slot, out ReflectionProbe probe) => _line.TakeNext(out slot, out probe);

    /// <summary>
    /// Puts a probe <see cref="TakeNext"/> gave back at the front of the line, for a frame that never drew its capture.
    /// A probe since removed, or put in line again, is left as it is.
    /// </summary>
    public void PutBack(ReflectionProbeHandle handle, in ReflectionProbe probe)
    {
        if (Slot(handle, required: false) >= 0)
        {
            _line.PutFirst(handle.Index, probe);
        }
    }

    /// <summary>Forgets one probe, whose cube goes to the next probe added. The rest keep theirs.</summary>
    public void Remove(ReflectionProbeHandle handle)
    {
        int slot = Slot(handle, required: true);
        _held[slot] = false;
        _slots[slot] = null;
        _line.Drop(slot);
        Count--;
    }

    /// <summary>Forgets every probe. Surfaces reflect the sky again from the next frame.</summary>
    public void Clear()
    {
        Array.Clear(_slots);
        Array.Clear(_held);
        _line.Clear();
        Count = 0;
    }

    // The cube of a probe that was added and not removed since, or -1 for a handle that names none, which throws when
    // the probe is required.
    private int Slot(ReflectionProbeHandle handle, bool required)
    {
        if ((uint)handle.Index < (uint)Cubes.Capacity && _held[handle.Index])
        {
            return handle.Index;
        }

        return required
            ? throw new ArgumentException($"Reflection probe {handle.Index} does not exist.", nameof(handle))
            : -1;
    }

    /// <summary>
    /// Records the blur of a capture, <paramref name="capture"/> with its mips ready to sample, into each level of
    /// <paramref name="slot"/>'s cube, and shows <paramref name="probe"/> there from this frame on. Surfaces read the cube
    /// in their fragment shaders, so the blur waits for the frames before to finish reading it, and the frame's own
    /// lighting waits for the blur.
    /// </summary>
    public void RecordFilter(CommandBuffer cmd, int frameIndex, int slot, in ReflectionProbe probe, ImageView capture)
    {
        var api = _context.Api;
        var image = Cubes.Image;
        uint baseLayer = (uint)slot * 6;
        api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.General) { MipCount = Mips, BaseLayer = baseLayer, LayerCount = 6 },
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit),
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit));

        Span<ImageView> levels = stackalloc ImageView[Mips];
        for (int mip = 0; mip < Mips; mip++)
        {
            levels[mip] = Cubes.Level(slot, mip);
        }

        _filter.Record(cmd, frameIndex, capture, levels);

        api.Transition(cmd, new ImageTransition(image, ImageAspectFlags.ColorBit,
            ImageLayout.General, ImageLayout.ShaderReadOnlyOptimal) { MipCount = Mips, BaseLayer = baseLayer, LayerCount = 6 },
            (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit));
        _slots[slot] = probe;
    }

    public void Dispose()
    {
        _filter.Dispose();
        Cubes.Dispose();
    }
}
