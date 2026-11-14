using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// The volumetric fog's grid on the GPU and the two passes that fill it. volumetric_inject.comp
/// lights each cell into one of two volumes, reading the other, last frame's, as history, and
/// volumetric_integrate.comp sums each column into <see cref="Result"/>, which the lighting,
/// glass and particle passes look up. All three volumes stay in the general layout. They are
/// made the first time a frame draws the fog, at the scene's size, so a game without it spends
/// no memory on them, and freed by <see cref="Release"/> when the fog is switched off. Without
/// them <see cref="Result"/> is a single texel that lets everything through. See "Volumetric fog" in docs/design.md.
/// </summary>
internal sealed unsafe class VolumetricFogVolume : IDisposable
{
    private const Format VolumeFormat = Format.R16G16B16A16Sfloat;

    private sealed class Volume(Image image, GpuAllocation memory, ImageView view)
    {
        public readonly Image Image = image;
        public readonly GpuAllocation Memory = memory;
        public readonly ImageView View = view;
    }

    private readonly VulkanContext _context;
    private readonly Sampler _sampler;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet[] _sets = new DescriptorSet[2];
    private readonly PipelineLayout _pipelineLayout;
    private readonly LivePipeline _inject;
    private readonly LivePipeline _integrate;
    private readonly Volume _empty;
    private Volume[]? _injected;
    private Volume? _integrated;
    private (int X, int Y, int Z) _size;

    // Which of the two injected volumes this frame writes; the other holds last frame's.
    private int _current;

    /// <summary>Whether last frame drew into the volume, so its cells can be reprojected.</summary>
    public bool HistoryReady { get; private set; }

    /// <summary>Whether the volumes have been made.</summary>
    public bool HasTargets => _integrated is not null;

    /// <summary>The summed volume, or until it's made a texel that lets everything through.</summary>
    public ImageView Result => (_integrated ?? _empty).View;

    public VolumetricFogVolume(VulkanContext context, DescriptorSetLayout frameSetLayout, Sampler linearSampler)
    {
        _context = context;
        _sampler = linearSampler;
        var api = context.Api;

        _setLayout = context.CreateSetLayout(
        [
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(2, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(3, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
            new(4, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
        ]);
        _pool = context.CreateDescriptorPool(
            2,
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 6),
            new DescriptorPoolSize(DescriptorType.StorageImage, 4));
        context.AllocateSets(_pool, _setLayout, _sets);

        _pipelineLayout = context.CreatePipelineLayout([frameSetLayout, _setLayout]);
        _inject = PipelineFactory.CreateCompute(context, "volumetric_inject.comp", _pipelineLayout);
        _integrate = PipelineFactory.CreateCompute(context, "volumetric_integrate.comp", _pipelineLayout);

        // Nothing in front, and everything behind gets through.
        _empty = CreateVolume(1, 1, 1, new ClearColorValue(0f, 0f, 0f, 1f));
    }

    /// <summary>
    /// Points the inject pass at the sun's shadow cascades and the sky the fog takes its colour
    /// from. Only while no frame in flight uses them.
    /// </summary>
    public void SetSources(in DescriptorImageInfo sunShadow, in DescriptorImageInfo sky)
    {
        var shadowInfo = sunShadow;
        var skyInfo = sky;
        var writes = stackalloc WriteDescriptorSet[4];
        for (int i = 0; i < 2; i++)
        {
            writes[2 * i] = ImageWrite(_sets[i], 0, DescriptorType.CombinedImageSampler, &shadowInfo);
            writes[2 * i + 1] = ImageWrite(_sets[i], 1, DescriptorType.CombinedImageSampler, &skyInfo);
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 4, writes, 0, null);
    }

    /// <summary>
    /// Makes the volumes for a scene of this many pixels, after render scale, replacing any
    /// there were, and starts the history again. Only while no frame in flight uses them.
    /// </summary>
    public void SetSize(int sceneWidth, int sceneHeight)
    {
        var size = VolumetricFog.GridSize(sceneWidth, sceneHeight);
        if (HasTargets && size == _size)
        {
            return;
        }

        DestroyTargets();
        _size = size;
        var black = new ClearColorValue(0f, 0f, 0f, 0f);
        _injected = [CreateVolume(size.X, size.Y, size.Z, black), CreateVolume(size.X, size.Y, size.Z, black)];
        _integrated = CreateVolume(size.X, size.Y, size.Z, new ClearColorValue(0f, 0f, 0f, 1f));
        HistoryReady = false;

        // Each set writes one injected volume and reads the other as history.
        var summed = new DescriptorImageInfo(default, _integrated.View, ImageLayout.General);
        var history = stackalloc DescriptorImageInfo[2];
        var written = stackalloc DescriptorImageInfo[2];
        var writes = stackalloc WriteDescriptorSet[6];
        for (int i = 0; i < 2; i++)
        {
            history[i] = new DescriptorImageInfo(_sampler, _injected[1 - i].View, ImageLayout.General);
            written[i] = new DescriptorImageInfo(default, _injected[i].View, ImageLayout.General);
            writes[3 * i] = ImageWrite(_sets[i], 2, DescriptorType.CombinedImageSampler, &history[i]);
            writes[3 * i + 1] = ImageWrite(_sets[i], 3, DescriptorType.StorageImage, &written[i]);
            writes[3 * i + 2] = ImageWrite(_sets[i], 4, DescriptorType.StorageImage, &summed);
        }

        _context.Api.UpdateDescriptorSets(_context.Device, 6, writes, 0, null);
    }

    /// <summary>A frame that doesn't draw the fog leaves nothing for the next to reproject.</summary>
    public void Skip() => HistoryReady = false;

    /// <summary>
    /// Frees the volumes, so <see cref="Result"/> is the stand-in texel again until
    /// <see cref="SetSize"/> makes them anew, with no history. Only while no frame in flight uses
    /// them, and the frame sets must be pointed at <see cref="Result"/> again.
    /// </summary>
    public void Release()
    {
        DestroyTargets();
        HistoryReady = false;
    }

    /// <summary>
    /// Lights the cells and sums the columns. Must come after the sun's shadow maps, the light
    /// atlas and the cluster lists are ready for compute to read, and before anything looks the
    /// fog up. Fragment shaders may sample <see cref="Result"/> once it's done.
    /// </summary>
    public void Record(CommandBuffer cmd, DescriptorSet frameSet)
    {
        if (_injected is null)
        {
            throw new InvalidOperationException("The volumetric fog's volumes are made before it is drawn.");
        }

        var api = _context.Api;

        // Last frame's passes read the history and the sum, and wrote what is history now.
        Barrier(cmd,
            PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderSampledReadBit);

        var sets = stackalloc DescriptorSet[] { frameSet, _sets[_current] };
        uint groupsX = (uint)(_size.X + 7) / 8;
        uint groupsY = (uint)(_size.Y + 7) / 8;
        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _inject);
        api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, _pipelineLayout, 0, 2, sets, 0, null);
        api.CmdDispatch(cmd, groupsX, groupsY, (uint)_size.Z);

        Barrier(cmd,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageReadBit);

        api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, _integrate);
        api.CmdDispatch(cmd, groupsX, groupsY, 1);

        Barrier(cmd,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);

        _current = 1 - _current;
        HistoryReady = true;
    }

    public void Dispose()
    {
        var api = _context.Api;
        DestroyTargets();
        Destroy(_empty);
        _inject.Destroy();
        _integrate.Destroy();
        api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
    }

    private void DestroyTargets()
    {
        if (_injected is not null)
        {
            Destroy(_injected[0]);
            Destroy(_injected[1]);
            _injected = null;
        }

        if (_integrated is not null)
        {
            Destroy(_integrated);
            _integrated = null;
        }
    }

    private void Destroy(Volume volume)
    {
        var api = _context.Api;
        api.DestroyImageView(_context.Device, volume.View, null);
        api.DestroyImage(_context.Device, volume.Image, null);
        _context.Memory.Free(volume.Memory);
    }

    private static WriteDescriptorSet ImageWrite(DescriptorSet set, uint binding, DescriptorType type, DescriptorImageInfo* info) => new()
    {
        SType = StructureType.WriteDescriptorSet,
        DstSet = set,
        DstBinding = binding,
        DescriptorCount = 1,
        DescriptorType = type,
        PImageInfo = info,
    };

    private void Barrier(
        CommandBuffer cmd, PipelineStageFlags2 sourceStage, AccessFlags2 sourceAccess, PipelineStageFlags2 destinationStage, AccessFlags2 destinationAccess)
    {
        var barrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = sourceStage,
            SrcAccessMask = sourceAccess,
            DstStageMask = destinationStage,
            DstAccessMask = destinationAccess,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            MemoryBarrierCount = 1,
            PMemoryBarriers = &barrier,
        };
        _context.Api.CmdPipelineBarrier2(cmd, &dependency);
    }

    /// <summary>A 3D image the passes write and sample, in the general layout for good, cleared to a value.</summary>
    private Volume CreateVolume(int width, int height, int depth, ClearColorValue clear)
    {
        var api = _context.Api;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type3D,
            Format = VolumeFormat,
            Extent = new Extent3D((uint)width, (uint)height, (uint)depth),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        api.CreateImage(_context.Device, &imageInfo, null, out var image).Check();
        api.GetImageMemoryRequirements(_context.Device, image, out var requirements);
        var memory = _context.Memory.AllocateImage(requirements, MemoryPropertyFlags.DeviceLocalBit);
        api.BindImageMemory(_context.Device, image, memory.Memory, memory.Offset).Check();

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type3D,
            Format = VolumeFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        api.CreateImageView(_context.Device, &viewInfo, null, out var view).Check();

        _context.SubmitAndWait(cmd =>
        {
            api.Transition(cmd, image, ImageAspectFlags.ColorBit,
                ImageLayout.Undefined, ImageLayout.General,
                PipelineStageFlags2.None, AccessFlags2.None,
                PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit);
            var value = clear;
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
            api.CmdClearColorImage(cmd, image, ImageLayout.General, &value, 1, &range);
            api.Transition(cmd, image, ImageAspectFlags.ColorBit,
                ImageLayout.General, ImageLayout.General,
                PipelineStageFlags2.ClearBit, AccessFlags2.TransferWriteBit,
                PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
                AccessFlags2.ShaderSampledReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.ShaderStorageReadBit);
        });
        return new Volume(image, memory, view);
    }
}
