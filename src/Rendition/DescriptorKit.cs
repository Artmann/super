using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition;

/// <summary>
/// The samplers, set layouts and descriptor pools the renderer's own passes share: the frame's set, the lighting
/// pass's set, and the sets of one to six textures the fullscreen passes read.
/// </summary>
internal sealed unsafe class DescriptorKit : IDisposable
{
    private readonly VulkanContext _context;
    private readonly DescriptorPool _pool;
    private readonly DescriptorPool _lightingPool;

    public DescriptorKit(VulkanContext context)
    {
        _context = context;
        NearestSampler = CreateSampler(Filter.Nearest);
        LinearSampler = CreateSampler(Filter.Linear);

        // Frosted glass reads the blurrier mips of the scene copy.
        MipSampler = CreateSampler(Filter.Linear, mips: true);

        // Reversed-Z: a point is lit when it is at least as near the light as the map says.
        ShadowSampler = CreateSampler(Filter.Linear, compare: true);

        FrameSetLayout = CreateFrameSetLayout(context);
        // With the frame set's five, the lighting pass samples 23 textures, more than MoltenVK allows in ordinary sets.
        LightingSetLayout = CreateTextureLayout(18, updateAfterBind: true);
        SixTextureLayout = CreateTextureLayout(6);
        FourTextureLayout = CreateTextureLayout(4);
        TwoTextureLayout = CreateTextureLayout(2);
        OneTextureLayout = CreateTextureLayout(1);

        _pool = context.CreateDescriptorPool(
            FrameRing.Count + 12,
            new DescriptorPoolSize(DescriptorType.UniformBuffer, FrameRing.Count),
            new DescriptorPoolSize(DescriptorType.StorageBuffer, 10 * FrameRing.Count),
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 42 + 5 * FrameRing.Count));
        _lightingPool = context.CreateDescriptorPool(
            DescriptorPoolCreateFlags.UpdateAfterBindBit, 1, new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 18));
    }

    /// <summary>Reads the nearest texel, clamped at the edges.</summary>
    public Sampler NearestSampler { get; }

    /// <summary>Blends the nearest four texels, clamped at the edges.</summary>
    public Sampler LinearSampler { get; }

    /// <summary>Blends texels and mips, for the blurrier mips of the scene copy.</summary>
    public Sampler MipSampler { get; }

    /// <summary>Compares depth for the shadow maps, reversed-Z.</summary>
    public Sampler ShadowSampler { get; }

    /// <summary>The frame's set, as <see cref="CreateFrameSetLayout"/> lays it out.</summary>
    public DescriptorSetLayout FrameSetLayout { get; }

    /// <summary>The lighting pass's 18 textures, which can be updated while bound.</summary>
    public DescriptorSetLayout LightingSetLayout { get; }

    public DescriptorSetLayout SixTextureLayout { get; }

    public DescriptorSetLayout FourTextureLayout { get; }

    public DescriptorSetLayout TwoTextureLayout { get; }

    public DescriptorSetLayout OneTextureLayout { get; }

    /// <summary>A set from the shared pool.</summary>
    public DescriptorSet NewSet(DescriptorSetLayout layout) => _context.AllocateSet(_pool, layout);

    /// <summary>The lighting pass's set, from its own pool, which allows updates after binding.</summary>
    public DescriptorSet NewLightingSet() => _context.AllocateSet(_lightingPool, LightingSetLayout);

    // A sampler clamped at the edges, reading every mip with mips, and comparing depth with compare.
    private Sampler CreateSampler(Filter filter, bool mips = false, bool compare = false)
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = mips ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = mips ? Vk.LodClampNone : 0f,
            CompareEnable = compare,
            CompareOp = compare ? CompareOp.GreaterOrEqual : default,
        };
        _context.Api.CreateSampler(_context.Device, &info, null, out var sampler).Check();
        return sampler;
    }

    // Fragment-shader textures, one to a binding from 0.
    private DescriptorSetLayout CreateTextureLayout(int bindingCount, bool updateAfterBind = false)
    {
        var type = DescriptorType.CombinedImageSampler;
        var stages = ShaderStageFlags.FragmentBit;
        Span<DescriptorSetLayoutBinding> bindings = stackalloc DescriptorSetLayoutBinding[bindingCount];
        for (int i = 0; i < bindingCount; i++)
        {
            bindings[i] = new DescriptorSetLayoutBinding((uint)i, type, 1, stages);
        }

        return _context.CreateSetLayout(bindings, updateAfterBind);
    }

    /// <summary>
    /// The frame set's layout. The frame uniforms are at binding 0, the skin matrices at binding 1 and each copy's
    /// model matrix at binding 2, as Shaders/common.glsl, skinning.glsl and instances.glsl read them. Bindings 3 to 5
    /// hold the point and spot lights, their shadow tiles and the cluster lists, and 6 and 7 the shadow atlas, as
    /// Shaders/light_data.glsl and local_shadows.glsl read them, 8 the volumetric fog's summed volume, as
    /// volumetric.glsl reads it, 9 where each copy stood last frame, as instances.glsl reads it for motion vectors, and
    /// 10 where each copy reads its baked light. 11 holds the frame's suns and placed chunks and 12 the sky projected
    /// by SkySh, as chunks.glsl reads them, to relight every copy of a chunk's bake. 13 holds the reflection probes the
    /// view sees, as light_data.glsl reads them. 14 holds the clouds' shadow map, as cloud_shadow.glsl reads it, and 15
    /// their picture, which the lighting pass lays over the sky.
    /// </summary>
    private static DescriptorSetLayout CreateFrameSetLayout(VulkanContext context)
    {
        const ShaderStageFlags everyStage = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit;
        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.UniformBuffer, 1, everyStage),
            new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit),
            new(2, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit),
            new(3, DescriptorType.StorageBuffer, 1, everyStage),
            new(4, DescriptorType.StorageBuffer, 1, everyStage),
            new(5, DescriptorType.StorageBuffer, 1, everyStage),
            new(6, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit),
            new(7, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(8, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
            new(9, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit),
            new(10, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit),
            new(11, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit),
            new(12, DescriptorType.StorageBuffer, 1, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit),
            new(13, DescriptorType.StorageBuffer, 1, everyStage),
            new(14, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit),
            new(15, DescriptorType.CombinedImageSampler, 1, ShaderStageFlags.FragmentBit),
        };
        return context.CreateSetLayout(new ReadOnlySpan<DescriptorSetLayoutBinding>(bindings, 16));
    }

    public void Dispose()
    {
        var api = _context.Api;
        var device = _context.Device;
        api.DestroyDescriptorPool(device, _pool, null);
        api.DestroyDescriptorPool(device, _lightingPool, null);
        api.DestroyDescriptorSetLayout(device, OneTextureLayout, null);
        api.DestroyDescriptorSetLayout(device, TwoTextureLayout, null);
        api.DestroyDescriptorSetLayout(device, SixTextureLayout, null);
        api.DestroyDescriptorSetLayout(device, FourTextureLayout, null);
        api.DestroyDescriptorSetLayout(device, LightingSetLayout, null);
        api.DestroyDescriptorSetLayout(device, FrameSetLayout, null);
        api.DestroySampler(device, ShadowSampler, null);
        api.DestroySampler(device, LinearSampler, null);
        api.DestroySampler(device, MipSampler, null);
        api.DestroySampler(device, NearestSampler, null);
    }
}
