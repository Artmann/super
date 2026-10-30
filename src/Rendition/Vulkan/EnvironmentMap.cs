using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// Cube maps of the surrounding studio. Reflections holds one mip per roughness step,
/// and Irradiance holds the soft light that reaches a matte surface.
/// </summary>
internal sealed unsafe class EnvironmentMap : IDisposable
{
    private const uint ReflectionSize = 512;
    private const uint IrradianceSize = 32;
    private const int MaxPanels = 8;

    /// <summary>One roughness step per mip. REFLECTION_LODS in Shaders/lighting.frag must be one less.</summary>
    private const int ReflectionMips = 6;

    private const Format CubeFormat = Format.R16G16B16A16Sfloat;

    [StructLayout(LayoutKind.Sequential)]
    private struct GpuPanel
    {
        public Vector4 Direction;
        public Vector4 Right;
        public Vector4 Up;
        public Vector4 Radiance;
    }

    [InlineArray(MaxPanels)]
    private struct GpuPanelArray
    {
        private GpuPanel _first;
    }

    /// <summary>Must match the Environment block in Shaders/environment.comp.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuEnvironment
    {
        public Vector4 Ceiling;
        public Vector4 Horizon;
        public Vector4 Floor;
        public int PanelCount;
        private readonly int _padding0;
        private readonly int _padding1;
        private readonly int _padding2;
        public GpuPanelArray Panels;
    }

    /// <summary>Must match the Bake block in Shaders/environment.comp.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct BakeConstants
    {
        public int Irradiance;
        public float Roughness;
    }

    /// <summary>A cube image, a view for sampling it and one writable view per mip.</summary>
    private readonly struct Cube(Image image, DeviceMemory memory, ImageView sampled, ImageView[] mips)
    {
        public readonly Image Image = image;
        public readonly DeviceMemory Memory = memory;
        public readonly ImageView Sampled = sampled;
        public readonly ImageView[] Mips = mips;
    }

    private readonly VulkanContext _context;
    private readonly Cube _reflections;
    private readonly Cube _irradiance;
    private readonly GpuBuffer _uniforms;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet[] _sets = new DescriptorSet[ReflectionMips + 1];
    private readonly PipelineLayout _pipelineLayout;
    private readonly Pipeline _pipeline;

    public readonly Sampler Sampler;
    public ImageView Reflections => _reflections.Sampled;
    public ImageView Irradiance => _irradiance.Sampled;

    public EnvironmentMap(VulkanContext context)
    {
        _context = context;
        var api = context.Api;

        _reflections = CreateCube(ReflectionSize, ReflectionMips);
        _irradiance = CreateCube(IrradianceSize, 1);
        _uniforms = GpuBuffer.CreateHostVisible(context, (ulong)sizeof(GpuEnvironment), BufferUsageFlags.UniformBufferBit);

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = Vk.LodClampNone,
        };
        api.CreateSampler(context.Device, &samplerInfo, null, out Sampler).Check();

        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.UniformBuffer, 1, ShaderStageFlags.ComputeBit),
            new(1, DescriptorType.StorageImage, 1, ShaderStageFlags.ComputeBit),
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &layoutInfo, null, out _setLayout).Check();

        uint setCount = (uint)_sets.Length;
        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.UniformBuffer, setCount),
            new(DescriptorType.StorageImage, setCount),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = setCount,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        api.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool).Check();

        for (int i = 0; i < _sets.Length; i++)
        {
            _sets[i] = CreateBakeSet(i < ReflectionMips ? _reflections.Mips[i] : _irradiance.Mips[0]);
        }

        var pushRange = new PushConstantRange(ShaderStageFlags.ComputeBit, 0, (uint)sizeof(BakeConstants));
        var setLayout = _setLayout;
        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        api.CreatePipelineLayout(context.Device, &pipelineLayoutInfo, null, out _pipelineLayout).Check();
        _pipeline = PipelineFactory.CreateCompute(context, "environment.comp", _pipelineLayout);
    }

    /// <summary>Renders the studio into the cube maps. Each level runs separately to keep GPU work short.</summary>
    public void Bake(in EnvironmentDescription description)
    {
        WriteUniforms(description);

        for (int mip = 0; mip < ReflectionMips; mip++)
        {
            var constants = new BakeConstants { Roughness = mip / (float)(ReflectionMips - 1) };
            BakeLevel(_reflections.Image, mip, _sets[mip], ReflectionSize >> mip, constants);
        }

        BakeLevel(_irradiance.Image, 0, _sets[ReflectionMips], IrradianceSize, new BakeConstants { Irradiance = 1 });
    }

    public void Dispose()
    {
        var api = _context.Api;
        api.DestroyPipeline(_context.Device, _pipeline, null);
        api.DestroyPipelineLayout(_context.Device, _pipelineLayout, null);
        api.DestroyDescriptorPool(_context.Device, _pool, null);
        api.DestroyDescriptorSetLayout(_context.Device, _setLayout, null);
        api.DestroySampler(_context.Device, Sampler, null);
        _uniforms.Dispose();
        DestroyCube(_irradiance);
        DestroyCube(_reflections);
    }

    private void WriteUniforms(in EnvironmentDescription description)
    {
        var uniforms = (GpuEnvironment*)_uniforms.Mapped;
        uniforms->Ceiling = new Vector4(description.Ceiling, 0f);
        uniforms->Horizon = new Vector4(description.Horizon, 0f);
        uniforms->Floor = new Vector4(description.Floor, 0f);

        int count = Math.Min(description.Panels.Length, MaxPanels);
        uniforms->PanelCount = count;
        for (int i = 0; i < count; i++)
        {
            ref readonly var panel = ref description.Panels[i];
            var direction = Vector3.Normalize(panel.Direction);
            var worldUp = MathF.Abs(direction.Y) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
            var right = Vector3.Normalize(Vector3.Cross(worldUp, direction));
            uniforms->Panels[i] = new GpuPanel
            {
                Direction = new Vector4(direction, 0f),
                Right = new Vector4(right, panel.HalfWidth),
                Up = new Vector4(Vector3.Cross(direction, right), panel.HalfHeight),
                Radiance = new Vector4(panel.Radiance, 0f),
            };
        }
    }

    private void BakeLevel(Image image, int mip, DescriptorSet set, uint size, BakeConstants constants)
    {
        var api = _context.Api;
        var pipeline = _pipeline;
        var layout = _pipelineLayout;
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, (uint)mip, 1, 0, 6);

        _context.SubmitAndWait(cmd =>
        {
            Barrier(api, cmd, image, range, ImageLayout.Undefined, ImageLayout.General,
                PipelineStageFlags2.None, AccessFlags2.None,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit);

            var boundSet = set;
            var push = constants;
            api.CmdBindPipeline(cmd, PipelineBindPoint.Compute, pipeline);
            api.CmdBindDescriptorSets(cmd, PipelineBindPoint.Compute, layout, 0, 1, &boundSet, 0, null);
            api.CmdPushConstants(cmd, layout, ShaderStageFlags.ComputeBit, 0, (uint)sizeof(BakeConstants), &push);
            uint groups = (size + 7) / 8;
            api.CmdDispatch(cmd, groups, groups, 6);

            Barrier(api, cmd, image, range, ImageLayout.General, ImageLayout.ShaderReadOnlyOptimal,
                PipelineStageFlags2.ComputeShaderBit, AccessFlags2.ShaderStorageWriteBit,
                PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit);
        });
    }

    private static void Barrier(
        Vk api,
        CommandBuffer cmd,
        Image image,
        ImageSubresourceRange range,
        ImageLayout from,
        ImageLayout to,
        PipelineStageFlags2 sourceStage,
        AccessFlags2 sourceAccess,
        PipelineStageFlags2 destinationStage,
        AccessFlags2 destinationAccess)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = sourceStage,
            SrcAccessMask = sourceAccess,
            DstStageMask = destinationStage,
            DstAccessMask = destinationAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        api.CmdPipelineBarrier2(cmd, &dependency);
    }

    private DescriptorSet CreateBakeSet(ImageView target)
    {
        var api = _context.Api;
        var layout = _setLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        api.AllocateDescriptorSets(_context.Device, &allocInfo, out var set).Check();

        var bufferInfo = new DescriptorBufferInfo(_uniforms.Handle, 0, (ulong)sizeof(GpuEnvironment));
        var imageInfo = new DescriptorImageInfo(default, target, ImageLayout.General);
        var writes = stackalloc WriteDescriptorSet[]
        {
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 0,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.UniformBuffer,
                PBufferInfo = &bufferInfo,
            },
            new()
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = 1,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.StorageImage,
                PImageInfo = &imageInfo,
            },
        };
        api.UpdateDescriptorSets(_context.Device, 2, writes, 0, null);
        return set;
    }

    private Cube CreateCube(uint size, uint mipCount)
    {
        var api = _context.Api;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            Flags = ImageCreateFlags.CreateCubeCompatibleBit,
            ImageType = ImageType.Type2D,
            Format = CubeFormat,
            Extent = new Extent3D(size, size, 1),
            MipLevels = mipCount,
            ArrayLayers = 6,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = ImageUsageFlags.StorageBit | ImageUsageFlags.SampledBit,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        api.CreateImage(_context.Device, &imageInfo, null, out var image).Check();

        api.GetImageMemoryRequirements(_context.Device, image, out var requirements);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = requirements.Size,
            MemoryTypeIndex = _context.FindMemoryType(requirements.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        api.AllocateMemory(_context.Device, &allocInfo, null, out var memory).Check();
        api.BindImageMemory(_context.Device, image, memory, 0).Check();

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.TypeCube,
            Format = CubeFormat,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, mipCount, 0, 6),
        };
        api.CreateImageView(_context.Device, &viewInfo, null, out var sampled).Check();

        var mips = new ImageView[mipCount];
        for (uint mip = 0; mip < mipCount; mip++)
        {
            viewInfo.ViewType = ImageViewType.Type2DArray;
            viewInfo.SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, mip, 1, 0, 6);
            api.CreateImageView(_context.Device, &viewInfo, null, out mips[mip]).Check();
        }

        return new Cube(image, memory, sampled, mips);
    }

    private void DestroyCube(in Cube cube)
    {
        var api = _context.Api;
        foreach (var view in cube.Mips)
        {
            api.DestroyImageView(_context.Device, view, null);
        }

        api.DestroyImageView(_context.Device, cube.Sampled, null);
        api.DestroyImage(_context.Device, cube.Image, null);
        api.FreeMemory(_context.Device, cube.Memory, null);
    }
}
