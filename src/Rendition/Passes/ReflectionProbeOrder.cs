using System.Numerics;

namespace Rendition.Passes;

/// <summary>
/// Picks the frame's reflection probes and puts them in the order the shaders blend them. Probes
/// whose box the view can't see are left out. The rest go highest <see cref="ReflectionProbe.Priority"/>
/// first, then smallest box first, so a room's probe wins over the yard's round it. The clusters
/// list each probe by its place in this order, so the first one a pixel finds is the one it trusts
/// most. No Vulkan here, so it can be tested on its own.
/// </summary>
internal sealed class ReflectionProbeOrder
{
    /// <summary>
    /// The most probes a frame can have. Must match the 64 bits the clusters keep for them in
    /// Shaders/light_data.glsl.
    /// </summary>
    public const int MaxProbes = 64;

    private readonly int[] _order = new int[MaxProbes];
    private readonly float[] _volumes = new float[MaxProbes];

    /// <summary>How many probes the view sees, written to the front of the GPU list.</summary>
    public int Count { get; private set; }

    /// <summary>The nearest and furthest depth from the camera any kept probe's box reaches.</summary>
    public float NearDepth { get; private set; }
    public float FarDepth { get; private set; }

    /// <summary>
    /// Writes the probes the view sees to <paramref name="gpu"/>, in the order they blend.
    /// <paramref name="slots"/> holds every captured probe at its layer of the cube array, and
    /// null for a free layer.
    /// </summary>
    public void Prepare(ReadOnlySpan<ReflectionProbe?> slots, in Matrix4x4 view, in Matrix4x4 projection, Span<GpuReflectionProbe> gpu)
    {
        var frustum = new DetailView(view * projection, Vector3.Zero, 0f, 0f, 0f);
        Count = 0;
        NearDepth = float.MaxValue;
        FarDepth = float.MinValue;
        for (int slot = 0; slot < slots.Length && Count < MaxProbes; slot++)
        {
            if (slots[slot] is { } probe && frustum.CanSee(new Vector4((probe.BoxMin + probe.BoxMax) * 0.5f, (probe.BoxMax - probe.BoxMin).Length() * 0.5f)))
            {
                Widen(probe, view);
                Insert(slots, slot, probe);
            }
        }

        for (int i = 0; i < Count; i++)
        {
            var probe = slots[_order[i]]!.Value;
            gpu[i] = new GpuReflectionProbe
            {
                Position = new Vector4(probe.Position, probe.BlendDistance),
                BoxMin = new Vector4(probe.BoxMin, _order[i]),
                BoxMax = new Vector4(probe.BoxMax, 0f),
            };
        }
    }

    // Takes the depths of the box's eight corners into NearDepth and FarDepth.
    private void Widen(in ReflectionProbe probe, in Matrix4x4 view)
    {
        for (int corner = 0; corner < 8; corner++)
        {
            var point = new Vector3(
                (corner & 1) == 0 ? probe.BoxMin.X : probe.BoxMax.X,
                (corner & 2) == 0 ? probe.BoxMin.Y : probe.BoxMax.Y,
                (corner & 4) == 0 ? probe.BoxMin.Z : probe.BoxMax.Z);
            float depth = -Vector3.Transform(point, view).Z;
            NearDepth = MathF.Min(NearDepth, depth);
            FarDepth = MathF.Max(FarDepth, depth);
        }
    }

    // Puts a probe into the kept ones where it blends: higher priority first, then the smaller box. Equal
    // probes keep their slot order, as it only moves past those strictly behind.
    private void Insert(ReadOnlySpan<ReflectionProbe?> slots, int slot, in ReflectionProbe probe)
    {
        var size = probe.BoxMax - probe.BoxMin;
        float volume = size.X * size.Y * size.Z;
        int at = Count;
        while (at > 0)
        {
            float priority = slots[_order[at - 1]]!.Value.Priority;
            if (probe.Priority < priority || (probe.Priority == priority && volume >= _volumes[at - 1]))
            {
                break;
            }

            _order[at] = _order[at - 1];
            _volumes[at] = _volumes[at - 1];
            at--;
        }

        _order[at] = slot;
        _volumes[at] = volume;
        Count++;
    }
}
