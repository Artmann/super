using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// One descriptor set holding every texture and material. Shaders index it directly,
/// so a draw only has to say which material it uses, and thousands of materials cost
/// no extra binding work. Binding 0 holds the <see cref="Textures"/>, binding 1 the <see cref="Materials"/> and
/// binding 2 the <see cref="TextureArrays"/>.
/// </summary>
internal sealed unsafe class ResourceTable : IDisposable
{
    /// <summary>How many textures the table holds where the GPU allows it. See <see cref="BindlessSlots.Capacity"/>.</summary>
    public const uint WantedTextures = 1024;
    public const uint MaxTextureArrays = 256;

    /// <summary>Fragment shaders draw with the table; compute shaders that trace rays read it for what they hit.</summary>
    private const ShaderStageFlags Stages = ShaderStageFlags.FragmentBit | ShaderStageFlags.ComputeBit;

    private readonly VulkanContext _context;

    // Trilinear, anisotropic samplers: one that repeats past the edges, and one that clamps to them for masks, which
    // are atlases of islands: at the texture's edge an island must not pick up the far side.
    private readonly (Sampler Repeat, Sampler Clamp) _samplers;
    private readonly DescriptorPool _pool;
    private readonly GpuBuffer _materialBuffer;

    public readonly DescriptorSetLayout Layout;
    public readonly DescriptorSet Set;

    /// <summary>
    /// The textures, as many as <see cref="WantedTextures"/>, or fewer on a GPU that allows fewer descriptors in one
    /// set. MoltenVK allows 1212, so the table holds 955 textures there.
    /// </summary>
    public BindlessSlots Textures { get; }

    /// <summary>The texture arrays, <see cref="MaxTextureArrays"/> of them.</summary>
    public BindlessSlots TextureArrays { get; }

    public MaterialTable Materials { get; }

    /// <summary>The table's own plain textures, which can't be destroyed.</summary>
    public MaterialDefaults Defaults { get; }

    /// <summary>The textures a table holds on a GPU that allows <paramref name="perSetDescriptors"/> descriptors in one set.</summary>
    internal static uint TexturesFor(uint perSetDescriptors)
    {
        // The texture arrays and the materials' buffer share the set.
        const uint others = MaxTextureArrays + 1;
        const uint fewest = 256;
        uint room = perSetDescriptors > others ? perSetDescriptors - others : 0;
        return room >= fewest
            ? Math.Min(WantedTextures, room)
            : throw new InvalidOperationException(
                $"This GPU allows {perSetDescriptors} descriptors in one set, too few for the renderer's table of every texture.");
    }

    public ResourceTable(VulkanContext context)
    {
        _context = context;
        var properties11 = new PhysicalDeviceVulkan11Properties { SType = StructureType.PhysicalDeviceVulkan11Properties };
        var properties2 = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &properties11 };
        context.Api.GetPhysicalDeviceProperties2(context.PhysicalDevice, &properties2);
        uint maxTextures = TexturesFor(properties11.MaxPerSetDescriptors);
        _samplers = CreateSamplers(context, properties2.Properties.Limits.MaxSamplerAnisotropy);
        Layout = CreateLayout(context, maxTextures);
        _pool = context.CreateDescriptorPool(
            DescriptorPoolCreateFlags.UpdateAfterBindBit,
            1,
            new DescriptorPoolSize(DescriptorType.CombinedImageSampler, maxTextures + MaxTextureArrays),
            new DescriptorPoolSize(DescriptorType.StorageBuffer, 1));
        Set = context.AllocateSet(_pool, Layout);
        Textures = new BindlessSlots(context, (Set, 0), (int)maxTextures, _samplers, "textures");
        TextureArrays = new BindlessSlots(context, (Set, 2), (int)MaxTextureArrays, _samplers, "texture arrays");

        var white = AddTexture(SolidTexture(255, 255, 255));
        Defaults = new MaterialDefaults(
            white, AddTexture(SolidTexture(128, 128, 255)), AddTexture(SolidTexture(128, 128, 128)),
            AddTextureArray(new GpuTexture(context, [SolidTexture(255, 255, 255)])));
        Textures.Fallback = white.Index;
        TextureArrays.Fallback = Defaults.WhiteArray.Index;

        _materialBuffer = GpuBuffer.CreateHostVisible(context, MaterialTable.BufferSize, BufferUsageFlags.StorageBufferBit);
        Materials = new MaterialTable(_materialBuffer, Defaults);
        var bufferInfo = new DescriptorBufferInfo(_materialBuffer.Handle, 0, MaterialTable.BufferSize);
        var write = Descriptors.BufferWrite(Set, 1, &bufferInfo);
        context.Api.UpdateDescriptorSets(context.Device, 1, &write, 0, null);
    }

    // Trilinear, anisotropic samplers: one that repeats past the edges, and one that clamps to them.
    private static (Sampler Repeat, Sampler Clamp) CreateSamplers(VulkanContext context, float maxAnisotropy)
    {
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.Repeat,
            AnisotropyEnable = true,
            MaxAnisotropy = Math.Min(16f, maxAnisotropy),
            MaxLod = Vk.LodClampNone,
        };
        context.Api.CreateSampler(context.Device, &samplerInfo, null, out var repeat).Check();
        samplerInfo.AddressModeU = SamplerAddressMode.ClampToEdge;
        samplerInfo.AddressModeV = SamplerAddressMode.ClampToEdge;
        samplerInfo.AddressModeW = SamplerAddressMode.ClampToEdge;
        context.Api.CreateSampler(context.Device, &samplerInfo, null, out var clamp).Check();
        return (repeat, clamp);
    }

    // The table's set layout: the textures, the materials' buffer and the texture arrays.
    private static DescriptorSetLayout CreateLayout(VulkanContext context, uint maxTextures)
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.CombinedImageSampler, maxTextures, Stages),
            new(1, DescriptorType.StorageBuffer, 1, Stages),
            new(2, DescriptorType.CombinedImageSampler, MaxTextureArrays, Stages),
        };

        // Most of the texture arrays stay empty, which Vulkan only allows when told so, and
        // slots no frame in flight uses may be written while the set is bound. Update-after-bind
        // lets the table count against the limits for such sets, which every GPU sets far higher:
        // MoltenVK allows 16 samplers in an ordinary set, and half a million in one of these.
        const DescriptorBindingFlags sparse =
            DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.UpdateUnusedWhilePendingBit
            | DescriptorBindingFlags.UpdateAfterBindBit;
        var bindingFlags = stackalloc DescriptorBindingFlags[] { sparse, 0, sparse };
        var flagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = 3,
            PBindingFlags = bindingFlags,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            PNext = &flagsInfo,
            Flags = DescriptorSetLayoutCreateFlags.UpdateAfterBindPoolBit,
            BindingCount = 3,
            PBindings = bindings,
        };
        context.Api.CreateDescriptorSetLayout(context.Device, &layoutInfo, null, out var layout).Check();
        return layout;
    }

    /// <summary>
    /// Takes a slot for a texture and points it at the image, read past its edges as <paramref name="edges"/> says. A
    /// full table frees the texture and throws; see <see cref="BindlessSlots.HasRoom"/>.
    /// </summary>
    public TextureHandle AddTexture(GpuTexture texture, TextureEdges edges = TextureEdges.Repeat) =>
        new(Textures.Add(texture, edges == TextureEdges.Clamp));

    /// <summary>Waits for the GPU, so call this while loading rather than during a frame.</summary>
    public TextureHandle AddTexture(TextureData data) => AddTexture(new GpuTexture(_context, data));

    /// <summary>Takes a slot for a texture array, the same way as <see cref="AddTexture(GpuTexture, TextureEdges)"/>.</summary>
    public TextureArrayHandle AddTextureArray(GpuTexture texture) => new(TextureArrays.Add(texture));

    public void Dispose()
    {
        Textures.Dispose();
        TextureArrays.Dispose();
        _materialBuffer.Dispose();
        _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        _context.Api.DestroyDescriptorSetLayout(_context.Device, Layout, null);
        _context.Api.DestroySampler(_context.Device, _samplers.Repeat, null);
        _context.Api.DestroySampler(_context.Device, _samplers.Clamp, null);
    }

    private static TextureData SolidTexture(byte r, byte g, byte b) => new()
    {
        Width = 1,
        Height = 1,
        Kind = TextureKind.Linear,
        Mips = [[r, g, b, 255]],
    };
}
