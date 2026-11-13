using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// One part of a mesh as a ray sees it: the run of indices a draw item covers, and the material that decides whether
/// rays can pass through it.
/// </summary>
public readonly record struct TracePart(uint FirstIndex, uint IndexCount, MaterialHandle Material);

/// <summary>How rays treat an instance, from the materials of its parts.</summary>
[Flags]
public enum TraceFlags : byte
{
    None = 0,

    /// <summary>Some part has holes cut by its texture's alpha, so a hit there needs checking.</summary>
    Cutout = 1,

    /// <summary>Some part is see-through, such as glass or water.</summary>
    SeeThrough = 2,
}

/// <summary>
/// One placed mesh in the scene rays are traced through, with every part of it the frame draws. <see cref="Tints"/>
/// holds each part's draw tint, in the same order; when it's empty, every part is untinted (white).
/// </summary>
public readonly record struct TraceInstance(
    Matrix4x4 Transform, MeshHandle Mesh, ReadOnlyMemory<TracePart> Parts, TraceFlags Flags)
{
    /// <summary>Each part's draw tint, multiplied into what a ray sees of it; empty when nothing is tinted.</summary>
    public ReadOnlyMemory<Vector3> Tints { get; init; }

    /// <summary>The tint of part <paramref name="part"/>, white when <see cref="Tints"/> is empty.</summary>
    public Vector3 TintOf(int part) => Tints.IsEmpty ? Vector3.One : Tints.Span[part];
}

/// <summary>
/// One entry of a top-level structure's geometry table: a part of a mesh, and its draw's tint. A hit's entry is its
/// instance's custom index plus its geometry index.
/// </summary>
public readonly record struct TraceGeometry(MeshHandle Mesh, TracePart Part, Vector3 Tint);

/// <summary>
/// Chooses what in a frame's draws rays can hit. One is kept for the renderer's life and reuses its buffers, so a
/// frame that collects no more than the frames before it allocates nothing. See "Ray tracing" in docs/design.md.
/// </summary>
internal sealed unsafe class TraceScene(Func<MaterialHandle, TraceFlags> flagsOf)
{
    /// <summary>An instance being gathered: what it is, and where its parts go in the shared part array.</summary>
    private struct Placed
    {
        public MeshHandle Mesh;
        public Matrix4x4 Transform;
        public TraceFlags Flags;
        public int Start;
        public int Count;
        public int Filled;
    }

    private readonly Dictionary<(MeshHandle, Matrix4x4), int> _order = [];
    private readonly List<Placed> _placed = [];
    private readonly List<TraceInstance> _instances = [];

    // Per draw, the instance it belongs to, or -1 when it's left out.
    private int[] _instanceOf = [];

    // Every instance's parts and tints, one run per instance, which the instances' memories point into.
    private TracePart[] _parts = [];
    private Vector3[] _tints = [];

    /// <summary>
    /// One instance per distinct mesh and transform, in the order they're first drawn, with the parts in draw order.
    /// Ghosts are previews, not things; skinned and swimming draws move away from the shape their structure was built
    /// from; so all three are left out, along with draws of no triangles. The list and the parts it points into are
    /// reused, so they're valid until the next call.
    /// </summary>
    public IReadOnlyList<TraceInstance> Collect(ReadOnlySpan<DrawItem> draws)
    {
        _order.Clear();
        _placed.Clear();
        _instances.Clear();
        if (_instanceOf.Length < draws.Length)
        {
            Array.Resize(ref _instanceOf, Math.Max(draws.Length, _instanceOf.Length * 2));
        }

        // First pass: which instance each draw belongs to, and how many parts each instance has.
        int partCount = 0;
        for (int d = 0; d < draws.Length; d++)
        {
            ref readonly var draw = ref draws[d];
            if (draw.Ghost is not null || draw.Bones.Count != 0 || draw.Swim.Amplitude != 0f || draw.IndexCount == 0)
            {
                _instanceOf[d] = -1;
                continue;
            }

            var key = (draw.Mesh, draw.Transform);
            if (!_order.TryGetValue(key, out int index))
            {
                index = _placed.Count;
                _order[key] = index;
                _placed.Add(new Placed { Mesh = draw.Mesh, Transform = draw.Transform });
            }

            ref var placed = ref CollectionsMarshal.AsSpan(_placed)[index];
            placed.Count++;
            placed.Flags |= flagsOf(draw.Material);
            _instanceOf[d] = index;
            partCount++;
        }

        if (_parts.Length < partCount)
        {
            int size = Math.Max(partCount, _parts.Length * 2);
            _parts = new TracePart[size];
            _tints = new Vector3[size];
        }

        var all = CollectionsMarshal.AsSpan(_placed);
        int start = 0;
        foreach (ref var placed in all)
        {
            placed.Start = start;
            start += placed.Count;
        }

        // Second pass: each draw's part and tint into its instance's run, in draw order.
        for (int d = 0; d < draws.Length; d++)
        {
            int index = _instanceOf[d];
            if (index < 0)
            {
                continue;
            }

            ref readonly var draw = ref draws[d];
            ref var placed = ref all[index];
            int at = placed.Start + placed.Filled++;
            _parts[at] = new TracePart(draw.FirstIndex, draw.IndexCount, draw.Material);
            _tints[at] = draw.Tint ?? Vector3.One;
        }

        foreach (ref readonly var placed in all)
        {
            _instances.Add(new TraceInstance(
                placed.Transform, placed.Mesh, _parts.AsMemory(placed.Start, placed.Count), placed.Flags)
            {
                Tints = _tints.AsMemory(placed.Start, placed.Count),
            });
        }

        return _instances;
    }

    /// <summary>
    /// System.Numerics multiplies row vectors (<c>v * M</c>, translation in <c>M41..M43</c>). Vulkan's 3×4 is row-major
    /// and acts on column vectors, so row r of the result is column r of <paramref name="transform"/>.
    /// </summary>
    public static TransformMatrixKHR ToVulkan(in Matrix4x4 transform)
    {
        var result = new TransformMatrixKHR();
        var m = new Span<float>(&result, 12);
        m[0] = transform.M11; m[1] = transform.M21; m[2] = transform.M31; m[3] = transform.M41;
        m[4] = transform.M12; m[5] = transform.M22; m[6] = transform.M32; m[7] = transform.M42;
        m[8] = transform.M13; m[9] = transform.M23; m[10] = transform.M33; m[11] = transform.M43;
        return result;
    }
}
