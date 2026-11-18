using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// How the scene rays see is written for the GPU: the instances of a top-level structure, the flags of a part, half
/// floats and alignment.
/// </summary>
internal static class TraceEncoding
{
    /// <summary>
    /// The stages that trace rays and read hit data: mesh bytes through buffer addresses and textures through the
    /// resource table.
    /// </summary>
    public const PipelineStageFlags2 TraceStages = PipelineStageFlags2.ComputeShaderBit;

    /// <summary>A see-through instance has this bit of its mask clear, so a ray can skip it with a cull mask.</summary>
    public const byte SeeThroughMaskBit = 0x02;

    /// <summary>The largest custom index an instance can have, and so the last geometry it can start at: 24 bits.</summary>
    private const uint MaxCustomIndex = (1u << 24) - 1;

    /// <summary>
    /// Fills in one Vulkan instance per trace instance, and appends their parts to the geometry table they index. Each
    /// instance's custom index is where its first part sits in the table, so a hit's entry is its custom index plus its
    /// geometry index.
    /// </summary>
    public static void Pack(
        IReadOnlyList<TraceInstance> instances,
        ReadOnlySpan<ulong> bottomLevels,
        Span<AccelerationStructureInstanceKHR> into,
        List<TraceGeometry> geometries)
    {
        uint first = (uint)geometries.Count;
        for (int i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            if (first + (ulong)instance.Parts.Length > MaxCustomIndex + 1ul)
            {
                throw new InvalidOperationException(
                    $"The scene has more than {MaxCustomIndex + 1} traced parts, which is more than an instance's custom index can count.");
            }

            into[i] = new AccelerationStructureInstanceKHR
            {
                Transform = TraceScene.ToVulkan(instance.Transform),
                InstanceCustomIndex = first,
                Mask = (instance.Flags & TraceFlags.SeeThrough) != 0 ? 0xFFu & ~(uint)SeeThroughMaskBit : 0xFFu,
                InstanceShaderBindingTableRecordOffset = 0,
                AccelerationStructureReference = bottomLevels[i],
            };
            var parts = instance.Parts.Span;
            for (int p = 0; p < parts.Length; p++)
            {
                geometries.Add(new TraceGeometry(instance.Mesh, parts[p], instance.TintOf(p)));
            }

            first += (uint)instance.Parts.Length;
        }
    }

    /// <summary>Two floats as half floats in one word, as GLSL's unpackHalf2x16 reads them: the first in the low half.</summary>
    internal static uint Half2(float low, float high) =>
        BitConverter.HalfToUInt16Bits((Half)low) | ((uint)BitConverter.HalfToUInt16Bits((Half)high) << 16);

    /// <summary>How rays treat a material: whether it has holes to check, and whether it's see-through.</summary>
    public static TraceFlags TraceFlagsFor(bool cutouts, MaterialFlags flags) =>
        (cutouts ? TraceFlags.Cutout : TraceFlags.None)
        | ((flags & MaterialFlags.Transparent) != 0 ? TraceFlags.SeeThrough : TraceFlags.None);

    /// <summary>
    /// Opaque lets rays skip the any-hit check. A surface with cut-outs or a see-through one needs that check, so it
    /// gets no flags.
    /// </summary>
    public static GeometryFlagsKHR GeometryFlagsFor(bool cutouts, MaterialFlags flags) =>
        cutouts || (flags & MaterialFlags.Transparent) != 0 ? GeometryFlagsKHR.None : GeometryFlagsKHR.OpaqueBitKhr;

    /// <summary>The smallest multiple of <paramref name="alignment"/> that is at least <paramref name="value"/>.</summary>
    public static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) / alignment * alignment;

    /// <summary>The instances in a buffer at <paramref name="instances"/>, as the one geometry of a top-level structure.</summary>
    public static AccelerationStructureGeometryKHR InstancesGeometry(ulong instances) => new()
    {
        SType = StructureType.AccelerationStructureGeometryKhr,
        GeometryType = GeometryTypeKHR.InstancesKhr,
        Geometry = new AccelerationStructureGeometryDataKHR
        {
            Instances = new AccelerationStructureGeometryInstancesDataKHR
            {
                SType = StructureType.AccelerationStructureGeometryInstancesDataKhr,
                ArrayOfPointers = false,
                Data = new DeviceOrHostAddressConstKHR { DeviceAddress = instances },
            },
        },
    };
}
