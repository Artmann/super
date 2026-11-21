using System.Numerics;
using Silk.NET.Vulkan;
using Rendition.Passes;
using Rendition.Vulkan;

namespace Rendition;

/// <summary>The frames that can be in flight at once, one for each slot of the <see cref="FrameRing"/>.</summary>
internal sealed unsafe class FrameSlots
{
    /// <summary>The most skin matrices one frame can hold across every skinned draw.</summary>
    public const int MaxBones = 16384;

    private readonly VulkanContext _context;
    private readonly DescriptorKit _kit;
    private readonly FrameSlot[] _slots = new FrameSlot[FrameRing.Count];

    public FrameSlots(VulkanContext context, DescriptorKit kit)
    {
        _context = context;
        _kit = kit;
        for (int i = 0; i < FrameRing.Count; i++)
        {
            _slots[i] = CreateFrame();
        }
    }

    public ref FrameSlot this[int slot] => ref _slots[slot];

    /// <summary>The shadow atlas, read once with a depth comparison and once as plain depth, in every slot's set.</summary>
    public void WriteAtlas(ImageView atlas)
    {
        var compare = new DescriptorImageInfo(_kit.ShadowSampler, atlas, ImageLayout.ShaderReadOnlyOptimal);
        var depth = new DescriptorImageInfo(_kit.NearestSampler, atlas, ImageLayout.ShaderReadOnlyOptimal);
        var writes = stackalloc WriteDescriptorSet[]
        {
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstBinding = 6,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &compare,
            },
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstBinding = 7,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &depth,
            },
        };
        foreach (ref var frame in _slots.AsSpan())
        {
            writes[0].DstSet = frame.Set;
            writes[1].DstSet = frame.Set;
            _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
        }
    }

    /// <summary>The sky's projection, which relit chunks read, in every slot's set.</summary>
    public void WriteSkySh(SkySh skySh)
    {
        foreach (ref var frame in _slots.AsSpan())
        {
            var skyShInfo = new DescriptorBufferInfo(skySh.Handle, 0, SkySh.Size);
            var skyShWrite = Descriptors.BufferWrite(frame.Set, 12, &skyShInfo);
            _context.Api.UpdateDescriptorSets(_context.Device, 1, &skyShWrite, 0, null);
        }
    }

    /// <summary>The volumetric fog's summed volume, or the texel that stands in until it's made, in every slot's set.</summary>
    public void WriteVolumetric(ImageView result)
    {
        var info = new DescriptorImageInfo(_kit.LinearSampler, result, ImageLayout.General);
        foreach (ref var frame in _slots.AsSpan())
        {
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = frame.Set,
                DstBinding = 8,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &info,
            };
            _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
        }
    }

    /// <summary>
    /// The clouds' shadow map and their picture, or the texels that stand in until they're made, in every slot's set.
    /// </summary>
    public void WriteClouds(ImageView shadow, ImageView picture)
    {
        var infos = stackalloc DescriptorImageInfo[]
        {
            new(_kit.LinearSampler, shadow, ImageLayout.General),
            new(_kit.LinearSampler, picture, ImageLayout.General),
        };
        var writes = stackalloc WriteDescriptorSet[2];
        foreach (ref var frame in _slots.AsSpan())
        {
            writes[0] = Descriptors.ImageWrite(frame.Set, 14, DescriptorType.CombinedImageSampler, &infos[0]);
            writes[1] = Descriptors.ImageWrite(frame.Set, 15, DescriptorType.CombinedImageSampler, &infos[1]);
            _context.Api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
        }
    }

    /// <summary>
    /// Empties this frame's model matrices for a frame of at most <paramref name="copies"/>
    /// draws, growing the buffer first if they won't fit. Must happen after the frame's
    /// fence and before its commands are recorded, since they bind the descriptor.
    /// </summary>
    public void ResetInstances(in FrameSlot frame, int copies)
    {
        if (frame.Instances.Reset(copies))
        {
            WriteInstanceDescriptor(frame);
        }
    }

    /// <summary>
    /// Copies a frame's skin matrices to where the vertex shaders read them. Must happen
    /// after the frame's fence, since the buffer is written in place.
    /// </summary>
    public static void WriteBones(in FrameSlot frame, ReadOnlySpan<Matrix4x4> bones)
    {
        if (bones.Length > MaxBones)
        {
            throw new ArgumentException($"A frame can have at most {MaxBones} bone matrices, not {bones.Length}.", nameof(bones));
        }

        bones.CopyTo(new Span<Matrix4x4>(frame.Bones.Mapped, bones.Length));
    }

    public void Dispose()
    {
        foreach (ref var frame in _slots.AsSpan())
        {
            frame.Dispose(_context.Api, _context.Device);
        }
    }

    private FrameSlot CreateFrame()
    {
        var api = _context.Api;
        var frame = new FrameSlot();

        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.TransientBit,
            QueueFamilyIndex = _context.QueueFamily,
        };
        api.CreateCommandPool(_context.Device, &poolInfo, null, out frame.CommandPool).Check();

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = frame.CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        api.AllocateCommandBuffers(_context.Device, &allocInfo, out frame.CommandBuffer).Check();

        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit, // The first frame has nothing to wait for.
        };
        api.CreateFence(_context.Device, &fenceInfo, null, out frame.InFlight).Check();

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        api.CreateSemaphore(_context.Device, &semaphoreInfo, null, out frame.ImageAvailable).Check();

        CreateFrameBuffers(ref frame);
        frame.Set = _kit.NewSet(_kit.FrameSetLayout);
        WriteFrameDescriptors(frame);
        frame.Instances = new InstanceBuffer(_context);
        WriteInstanceDescriptor(frame);
        return frame;
    }

    // The frame's uniforms and the buffers its shaders read: bones, lights, shadow tiles, clusters, chunks and probes.
    private void CreateFrameBuffers(ref FrameSlot frame)
    {
        frame.Uniforms = GpuBuffer.CreateHostVisible(
            _context, (ulong)sizeof(FrameUniforms), BufferUsageFlags.UniformBufferBit);
        // This frame's bone matrices, then last frame's for motion vectors.
        frame.Bones = GpuBuffer.CreateHostVisible(
            _context, (ulong)(2 * MaxBones * sizeof(Matrix4x4)), BufferUsageFlags.StorageBufferBit);
        frame.Lights = GpuBuffer.CreateHostVisible(
            _context, (ulong)(LocalLight.MaxPerFrame * sizeof(GpuLocalLight)), BufferUsageFlags.StorageBufferBit);
        frame.ShadowTiles = GpuBuffer.CreateHostVisible(
            _context, (ulong)(LocalLights.MaxTiles * sizeof(GpuShadowTile)), BufferUsageFlags.StorageBufferBit);
        frame.Clusters = new GpuBuffer(
            _context, LocalLights.ClusterBytes, BufferUsageFlags.StorageBufferBit, MemoryPropertyFlags.DeviceLocalBit);
        frame.Chunks = GpuBuffer.CreateHostVisible(
            _context, (ulong)(ChunkRelight.HeaderBytes + BakeFiles.MaxChunks * sizeof(GpuChunk)), BufferUsageFlags.StorageBufferBit);
        frame.ReflectionProbes = GpuBuffer.CreateHostVisible(
            _context, (ulong)(ReflectionProbeOrder.MaxProbes * sizeof(GpuReflectionProbe)), BufferUsageFlags.StorageBufferBit);
    }

    // Points the frame's set at its buffers, as CreateFrameSetLayout lays them out.
    private void WriteFrameDescriptors(in FrameSlot frame)
    {
        var chunksInfo = new DescriptorBufferInfo(frame.Chunks.Handle, 0, frame.Chunks.Size);
        var uniformInfo = new DescriptorBufferInfo(frame.Uniforms.Handle, 0, (ulong)sizeof(FrameUniforms));
        var bonesInfo = new DescriptorBufferInfo(frame.Bones.Handle, 0, frame.Bones.Size);
        var lightsInfo = new DescriptorBufferInfo(frame.Lights.Handle, 0, frame.Lights.Size);
        var tilesInfo = new DescriptorBufferInfo(frame.ShadowTiles.Handle, 0, frame.ShadowTiles.Size);
        var clustersInfo = new DescriptorBufferInfo(frame.Clusters.Handle, 0, frame.Clusters.Size);
        var probesInfo = new DescriptorBufferInfo(frame.ReflectionProbes.Handle, 0, frame.ReflectionProbes.Size);
        var writes = stackalloc WriteDescriptorSet[]
        {
            Descriptors.BufferWrite(frame.Set, 0, &uniformInfo, DescriptorType.UniformBuffer),
            Descriptors.BufferWrite(frame.Set, 1, &bonesInfo),
            Descriptors.BufferWrite(frame.Set, 3, &lightsInfo),
            Descriptors.BufferWrite(frame.Set, 4, &tilesInfo),
            Descriptors.BufferWrite(frame.Set, 5, &clustersInfo),
            Descriptors.BufferWrite(frame.Set, 11, &chunksInfo),
            Descriptors.BufferWrite(frame.Set, 13, &probesInfo),
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 7, writes, 0, null);
    }

    private void WriteInstanceDescriptor(in FrameSlot frame)
    {
        var info = frame.Instances.ModelsInfo;
        var previousInfo = frame.Instances.PreviousInfo;
        var lightmapInfo = frame.Instances.LightmapInfo;
        var writes = stackalloc WriteDescriptorSet[]
        {
            Descriptors.BufferWrite(frame.Set, 2, &info),
            Descriptors.BufferWrite(frame.Set, 9, &previousInfo),
            Descriptors.BufferWrite(frame.Set, 10, &lightmapInfo),
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 3, writes, 0, null);
    }
}
