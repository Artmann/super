using System.Numerics;
using Rendition.Passes;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Captured views of the scene, held as an array of cube maps with one mip per roughness
/// step. The renderer draws each face and this class blurs them into the mips.
/// </summary>
internal sealed unsafe class ReflectionProbes : IDisposable
{
    public const uint FaceSize = 256;

    /// <summary>Same roughness steps as the studio. See REFLECTION_LODS in Shaders/brdf.glsl.</summary>
    private const int Mips = 6;

    private const Format CubeFormat = GBuffer.HdrFormat;

    /// <summary>Must match the Filter block in Shaders/reflection_probe.comp.</summary>
    private struct FilterConstants
    {
        public float Roughness;
    }

    private readonly VulkanContext _context;
    private readonly Sampler _sampler;
    private readonly Image _image;
    private readonly GpuAllocation _memory;
    private readonly ImageView[] _levels = new ImageView[FrameUniforms.MaxProbes * Mips];
    private readonly Image _source;
    private readonly GpuAllocation _sourceMemory;
    private readonly ImageView _sourceView;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _set;
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _pipeline;
    private readonly GpuReflectionProbe[] _probes = new GpuReflectionProbe[FrameUniforms.MaxProbes];

    public readonly ImageView View;
    public int Count { get; private set; }

    public ReflectionProbes(VulkanContext context, Sampler sampler)
    {
        _context = context;
        _sampler = sampler;
        var api = context.Api;

        uint layers = FrameUniforms.MaxProbes * 6;
        (_image, _memory) = CreateCubes(Mips, layers, ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit);
        (_source, _sourceMemory) = CreateCubes(1, 6, ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _image,
            ViewType = ImageViewType.TypeCubeArray,
            Format = CubeFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, Mips, 0, layers),
        };
        api.CreateImageView(context.Device, &viewInfo, null, out View).Check();

        viewInfo.ViewType = ImageViewType.Type2DArray;
        for (int probe = 0; probe < FrameUniforms.MaxProbes; probe++)
        {
            for (int mip = 0; mip < Mips; mip++)
            {
                viewInfo.SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, (uint)mip, 1, (uint)probe * 6, 6);
                api.CreateImageView(context.Device, &viewInfo, null, out _levels[probe * Mips + mip]).Check();
            }
        }

        viewInfo.Image = _source;
        viewInfo.ViewType = ImageViewType.TypeCube;
        viewInfo.SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 6);
        api.CreateImageView(context.Device, &viewInfo, null, out _sourceView).Check();

        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &layoutInfo, null, out _setLayout).Check();

        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.CombinedImageSampler, 1),
            new(DescriptorType.StorageImage, 1),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        api.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool).Check();

        var setLayout = _setLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        api.AllocateDescriptorSets(context.Device, &allocInfo, out _set).Check();

        var pushRange = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FilterConstants));
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        api.CreatePipelineLayout(context.Device, &pipelineLayoutInfo, null, out _pipelineLayout).Check();
        _pipeline = PipelineFactory.CreateCompute(context, "reflection_probe.comp", _pipelineLayout);

        // The lighting pass binds every probe from the start, captured or not.
        var image = _image;
        context.SubmitAndWait(cmd => api.Transition(cmd, image, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.None, AccessFlags2.None,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
            mipCount: Mips, layerCount: layers));
    }

    public void CopyToUniforms(FrameUniforms* uniforms)
    {
        uniforms->ProbeCount = Count;
        for (int i = 0; i < Count; i++)
        {
            uniforms->Probes[i] = _probes[i];
        }
    }

    /// <summary>Copies one finished face out of the image the scene was drawn to.</summary>
    public void RecordFace(CommandBuffer cmd, Image rendered, int face)
    {
        var api = _context.Api;
        api.Transition(cmd, rendered, ImageAspectFlags.ColorBit,
            ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferReadBit);
        api.Transition(cmd, _source, ImageAspectFlags.ColorBit,
            ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
            PipelineStageFlags2.None, AccessFlags2.None,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
            baseLayer: (uint)face);

        var region = new ImageCopy
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, (uint)face, 1),
            Extent = new Extent3D(FaceSize, FaceSize, 1),
        };
        api.CmdCopyImage(cmd, rendered, ImageLayout.TransferSrcOptimal, _source, ImageLayout.TransferDstOptimal, 1, &region);

        api.Transition(cmd, _source, ImageAspectFlags.ColorBit,
            ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.CopyBit, AccessFlags2.TransferWriteBit,
            PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderSampledReadBit,
            baseLayer: (uint)face);
    }

    /// <summary>Blurs the six captured faces into a new probe.</summary>
    public ReflectionProbeHandle Add(in ReflectionProbe probe)
    {
        if (Count == FrameUniforms.MaxProbes)
        {
            throw new InvalidOperationException($"Only {FrameUniforms.MaxProbes} reflection probes are supported.");
        }

        int index = Count;
        for (int mip = 0; mip < Mips; mip++)
        {
            FilterLevel(index, mip);
        }

        _probes[index] = new GpuReflectionProbe
        {
            Position = new Vector4(probe.Position, probe.BlendDistance),
            BoxMin = new Vector4(probe.BoxMin, 0f),
            BoxMax = new Vector4(probe.BoxMax, 0f),
        };
        Count++;
        return new ReflectionProbeHandle(index);
    }

    public void Dispose()
    {
        var api = _context.Api;
        api.DestroyPipeline(_context.Device, _pipeline, null);
        api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
        foreach (var level in _levels)
        {
            api.DestroyImageView(_context.Device, level, null);
        }

        api.DestroyImageView(_context.Device, View, null);
        api.DestroyImageView(_context.Device, _sourceView, null);
        api.DestroyImage(_context.Device, _image, null);
        api.DestroyImage(_context.Device, _source, null);
        _context.Memory.Free(_memory);
        _context.Memory.Free(_sourceMemory);
    }

    private void FilterLevel(int probe, int mip)
    {
        var api = _context.Api;

        var sourceInfo = new DescriptorImageInfo(_sampler, _sourceView, ImageLayout.ShaderReadOnlyOptimal);
        var targetInfo = new DescriptorImageInfo(default, _levels[probe * Mips + mip], ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[]
        {
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &sourceInfo,
            },
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _set,
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &targetInfo,
            },
        };
        api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);

        var image = _image;
        var set = _set;
        var layout = _pipelineLayout;
        var pipeline = _pipeline;
        uint baseLayer = (uint)probe * 6;
        uint size = FaceSize >> mip;
        var constants = new FilterConstants { Roughness = mip / (float)(Mips - 1) };

        // Each level runs separately to keep GPU work short.
        _context.SubmitAndWait(cmd =>
        {
            api.Transition(cmd, image, ImageAspectFlags.ColorBit,
                ImageLayout.ShaderReadOnlyOptimal, ImageLayout.General,
                PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
                baseMip: (uint)mip, baseLayer: baseLayer, layerCount: 6);

            var boundSet = set;
            var push = constants;
            api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
            api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, layout, 0, 1, &boundSet, 0, null);
            api.CmdPushConstants(cmd, layout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(FilterConstants), &push);
            uint groups = (size + 7) / 8;
            api.CmdDispatch(cmd, groups, groups, 6);

            api.Transition(cmd, image, ImageAspectFlags.ColorBit,
                ImageLayout.General, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit,
                baseMip: (uint)mip, baseLayer: baseLayer, layerCount: 6);
        });
    }

    private (Image Image, GpuAllocation Memory) CreateCubes(uint mips, uint layers, ImageUsageFlags usage)
    {
        var api = _context.Api;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            Flags = ImageCreateFlags.CreateCubeCompatibleBit,
            ImageType = ImageType.Type2D,
            Format = CubeFormat,
            Extent = new Extent3D(FaceSize, FaceSize, 1),
            MipLevels = mips,
            ArrayLayers = layers,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        api.CreateImage(_context.Device, &imageInfo, null, out var image).Check();

        api.GetImageMemoryRequirements(_context.Device, image, out var requirements);
        var memory = _context.Memory.AllocateImage(requirements, MemoryPropertyFlags.DeviceLocalBit);
        api.BindImageMemory(_context.Device, image, memory.Memory, memory.Offset).Check();
        return (image, memory);
    }
}
