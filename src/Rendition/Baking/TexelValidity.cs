using System.Runtime.InteropServices;
using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// Marks which texels of the <see cref="Baker"/>'s drawn pages are valid: a covered texel is invalid when more than
/// <see cref="BakeGpu.InvalidShare"/> of its <see cref="BakeGpu.ProbeRays"/> short rays hit the backs of walls.
/// </summary>
internal sealed unsafe class TexelValidity : IDisposable
{
    private readonly VulkanContext _context;
    private readonly RayTracing _tracing;
    private readonly BakeComputePass _pass;

    public TexelValidity(VulkanContext context, SceneStores stores, RayTracing tracing)
    {
        _context = context;
        _tracing = tracing;

        // The scene's top-level structure, its geometry table, the position and geometric normal read and the validity
        // written. The resource table is set 1, for the materials trace.glsl reads.
        var bindings = BakeGpu.Bindings(
            DescriptorType.AccelerationStructureKhr, DescriptorType.StorageBuffer,
            DescriptorType.StorageImage, DescriptorType.StorageImage, DescriptorType.StorageImage);
        _pass = new BakeComputePass(context, stores, bindings, (uint)sizeof(ValidityConstants), "bake_validity.comp");
    }

    /// <summary>
    /// Records the validity pass over the first <paramref name="count"/> pages against <paramref name="scene"/>, with
    /// its sets from <paramref name="pool"/>, then lets whatever comes next read what was written.
    /// </summary>
    public void Record(CommandBuffer cmd, TexelPages pages, int count, DescriptorPool pool, in TopLevelStructure scene)
    {
        var api = _context.Api;
        _tracing.ReadyToTrace(cmd);
        var probe = new ValidityConstants
        {
            Rays = BakeGpu.ProbeRays,
            RayLength = BakeGpu.ProbeRayLength,
            InvalidShare = BakeGpu.InvalidShare,
            Lift = BakeGpu.ProbeLift,
        };
        _pass.BindPipeline(cmd);
        for (int page = 0; page < count; page++)
        {
            var targets = pages[page];
            api.Transition(cmd, new ImageTransition(targets.Validity.Image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.General),
                (PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit, AccessFlags2.None),
                (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit));

            var set = _pass.AllocateSet(pool);
            WriteSet(set, scene, targets);
            _pass.BindSets(cmd, set);
            api.CmdPushConstants(cmd, _pass.Layout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(ValidityConstants), &probe);
            const uint groups = (LightmapLayout.PageSize + BakeGpu.GroupSize - 1) / BakeGpu.GroupSize;
            api.CmdDispatch(cmd, groups, groups, 1);
        }

        // Whatever comes next, the later bake stages or a copy, reads what was written.
        api.MemoryBarrier(cmd, (PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit),
            (PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.CopyBit,
                AccessFlags2.ShaderStorageReadBit | AccessFlags2.TransferReadBit));
    }

    private void WriteSet(DescriptorSet set, in TopLevelStructure scene, PageTargets targets)
    {
        var handle = scene.Handle;
        var structureInfo = new WriteDescriptorSetAccelerationStructureKHR
        {
            SType = StructureType.WriteDescriptorSetAccelerationStructureKhr,
            AccelerationStructureCount = 1,
            PAccelerationStructures = &handle,
        };
        var tableInfo = new DescriptorBufferInfo(scene.GeometryTable.Handle, 0, Vk.WholeSize);
        var images = stackalloc DescriptorImageInfo[]
        {
            new(default, targets.Position.View, ImageLayout.General),
            new(default, targets.GeometricNormal.View, ImageLayout.General),
            new(default, targets.Validity.View, ImageLayout.General),
        };
        var writes = stackalloc WriteDescriptorSet[5];
        writes[0] = Descriptors.StructureWrite(set, 0, &structureInfo);
        writes[1] = Descriptors.BufferWrite(set, 1, &tableInfo);
        for (int i = 0; i < 3; i++)
        {
            writes[2 + i] = Descriptors.ImageWrite(set, (uint)(2 + i), DescriptorType.StorageImage, &images[i]);
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 5, writes, 0, null);
    }

    public void Dispose() => _pass.Dispose();

    // Must match the push constants in bake_validity.comp.
    [StructLayout(LayoutKind.Sequential)]
    private struct ValidityConstants
    {
        public uint Rays;
        public float RayLength;
        public float InvalidShare;
        public float Lift;
    }
}
