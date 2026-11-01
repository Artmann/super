using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>
/// One descriptor set holding every texture and material. Shaders index it directly,
/// so a draw only has to say which material it uses, and thousands of materials cost
/// no extra binding work.
/// </summary>
internal sealed unsafe class ResourceTable : IDisposable
{
    public const uint MaxTextures = 1024;
    public const int MaxMaterials = 1024;

    /// <summary>Must match the Material struct in Shaders/material.glsl.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuMaterial
    {
        public Vector4 BaseColor;
        public float Metallic;
        public float Roughness;
        public float AlphaCutoff;
        public uint BaseColorTexture;
        public uint NormalTexture;
        public uint SurfaceTexture;
        public uint EmissiveTexture;
        public float Clearcoat;
        public Vector4 Emissive;
        public Vector4 ShaderParameters;
    }

    private readonly VulkanContext _context;
    private readonly Sampler _sampler;
    private readonly DescriptorPool _pool;
    private readonly GpuBuffer _materials;
    private readonly MaterialFlags[] _materialFlags = new MaterialFlags[MaxMaterials];
    private readonly int[] _materialShaders = new int[MaxMaterials];
    private readonly bool[] _materialCutouts = new bool[MaxMaterials];
    private readonly int[] _materialTextures = new int[MaxMaterials * TexturesPerMaterial];
    private readonly List<GpuTexture?> _textures = [];
    private readonly Stack<int> _freeTextures = [];
    private readonly Stack<int> _freeMaterials = [];
    private readonly TextureHandle _white;
    private readonly TextureHandle _flatNormal;
    private int _materialCount;

    /// <summary>Texture slots a material can name: base colour, normal, surface, emissive.</summary>
    public const int TexturesPerMaterial = 4;

    public readonly DescriptorSetLayout Layout;
    public readonly DescriptorSet Set;

    public ResourceTable(VulkanContext context)
    {
        _context = context;
        var api = context.Api;

        api.GetPhysicalDeviceProperties(context.PhysicalDevice, out var properties);
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
            MaxAnisotropy = Math.Min(16f, properties.Limits.MaxSamplerAnisotropy),
            MaxLod = Vk.LodClampNone,
        };
        api.CreateSampler(context.Device, &samplerInfo, null, out _sampler).Check();

        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new(0, DescriptorType.CombinedImageSampler, MaxTextures, ShaderStageFlags.FragmentBit),
            new(1, DescriptorType.StorageBuffer, 1, ShaderStageFlags.FragmentBit),
        };

        // Most of the texture array stays empty, which Vulkan only allows when told so, and
        // slots no frame in flight uses may be written while the set is bound.
        const DescriptorBindingFlags sparse =
            DescriptorBindingFlags.PartiallyBoundBit | DescriptorBindingFlags.UpdateUnusedWhilePendingBit;
        var bindingFlags = stackalloc DescriptorBindingFlags[] { sparse, 0 };
        var flagsInfo = new DescriptorSetLayoutBindingFlagsCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutBindingFlagsCreateInfo,
            BindingCount = 2,
            PBindingFlags = bindingFlags,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            PNext = &flagsInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        api.CreateDescriptorSetLayout(context.Device, &layoutInfo, null, out Layout).Check();

        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new(DescriptorType.CombinedImageSampler, MaxTextures),
            new(DescriptorType.StorageBuffer, 1),
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        api.CreateDescriptorPool(context.Device, &poolInfo, null, out _pool).Check();

        var layout = Layout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        api.AllocateDescriptorSets(context.Device, &allocInfo, out Set).Check();

        ulong materialsSize = (ulong)(MaxMaterials * sizeof(GpuMaterial));
        _materials = GpuBuffer.CreateHostVisible(context, materialsSize, BufferUsageFlags.StorageBufferBit);
        var bufferInfo = new DescriptorBufferInfo(_materials.Handle, 0, materialsSize);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = Set,
            DstBinding = 1,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &bufferInfo,
        };
        api.UpdateDescriptorSets(context.Device, 1, &write, 0, null);

        _white = AddTexture(SolidTexture(255, 255, 255));
        _flatNormal = AddTexture(SolidTexture(128, 128, 255));
    }

    public MaterialFlags GetFlags(MaterialHandle material) => _materialFlags[material.Index];

    public int GetShader(MaterialHandle material) => _materialShaders[material.Index];

    /// <summary>Whether the material's texture cuts holes in it.</summary>
    public bool HasCutouts(MaterialHandle material) => _materialCutouts[material.Index];

    /// <summary>
    /// Takes a slot for a texture and points it at the image. The slot is free in every
    /// frame in flight, which is what lets the set be written while bound.
    /// </summary>
    public TextureHandle AddTexture(GpuTexture texture)
    {
        int slot;
        if (_freeTextures.Count > 0)
        {
            slot = _freeTextures.Pop();
            _textures[slot] = texture;
        }
        else
        {
            if (_textures.Count == MaxTextures)
            {
                texture.Dispose();
                throw new InvalidOperationException($"The renderer holds at most {MaxTextures} textures.");
            }

            slot = _textures.Count;
            _textures.Add(texture);
        }

        WriteTextureDescriptor(slot, texture);
        return new TextureHandle(slot);
    }

    /// <summary>Waits for the GPU, so call this while loading rather than during a frame.</summary>
    public TextureHandle AddTexture(TextureData data) => AddTexture(new GpuTexture(_context, data));

    /// <summary>Whether the handle is one of the table's own textures, which can't be destroyed.</summary>
    public bool IsBuiltIn(TextureHandle texture) => texture == _white || texture == _flatNormal;

    public bool IsLive(TextureHandle texture) =>
        texture.Index >= 0 && texture.Index < _textures.Count && _textures[texture.Index] is not null;

    /// <summary>
    /// Frees a texture's slot once no frame can read it, pointing the slot at white so
    /// a stale material draws something harmless.
    /// </summary>
    public void ReleaseTexture(int slot)
    {
        var texture = _textures[slot];
        if (texture is null)
        {
            return;
        }

        texture.Dispose();
        _textures[slot] = null;
        WriteTextureDescriptor(slot, _textures[_white.Index]!);
        _freeTextures.Push(slot);
    }

    private void WriteTextureDescriptor(int slot, GpuTexture texture)
    {
        var imageInfo = new DescriptorImageInfo(_sampler, texture.View, ImageLayout.ShaderReadOnlyOptimal);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = Set,
            DstBinding = 0,
            DstArrayElement = (uint)slot,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &imageInfo,
        };
        _context.Api.UpdateDescriptorSets(_context.Device, 1, &write, 0, null);
    }

    public MaterialHandle AddMaterial(in Material material)
    {
        if (_freeMaterials.Count > 0)
        {
            int slot = _freeMaterials.Pop();
            WriteMaterial(slot, material);
            return new MaterialHandle(slot);
        }

        if (_materialCount == MaxMaterials)
        {
            throw new InvalidOperationException($"The renderer holds at most {MaxMaterials} materials.");
        }

        // Earlier frames only read earlier slots, so this write needs no synchronization.
        WriteMaterial(_materialCount, material);
        return new MaterialHandle(_materialCount++);
    }

    /// <summary>Lets a material slot be reused. Call once no frame in flight reads it.</summary>
    public void ReleaseMaterial(int slot) => _freeMaterials.Push(slot);

    /// <summary>The texture slots a material names, or -1 where it uses a built-in one.</summary>
    public ReadOnlySpan<int> TexturesOf(MaterialHandle material) =>
        _materialTextures.AsSpan(material.Index * TexturesPerMaterial, TexturesPerMaterial);

    /// <summary>
    /// Changes a material in place. A frame still on the GPU may see the new values,
    /// which at worst shows them one frame early.
    /// </summary>
    public void UpdateMaterial(MaterialHandle handle, in Material material)
    {
        if (handle.Index < 0 || handle.Index >= _materialCount)
        {
            throw new ArgumentException("The material does not exist.", nameof(handle));
        }

        WriteMaterial(handle.Index, material);
    }

    private void WriteMaterial(int index, in Material material)
    {
        ((GpuMaterial*)_materials.Mapped)[index] = new GpuMaterial
        {
            BaseColor = material.BaseColor,
            Metallic = material.Metallic,
            Roughness = material.Roughness,
            AlphaCutoff = material.AlphaCutoff,
            BaseColorTexture = (uint)(material.BaseColorTexture ?? _white).Index,
            NormalTexture = (uint)(material.NormalTexture ?? _flatNormal).Index,
            SurfaceTexture = (uint)(material.SurfaceTexture ?? _white).Index,
            EmissiveTexture = (uint)(material.EmissiveTexture ?? _white).Index,
            Clearcoat = material.Clearcoat,
            Emissive = new Vector4(material.Emissive, 0f),
            ShaderParameters = material.ShaderParameters,
        };
        _materialFlags[index] = material.Flags;
        _materialShaders[index] = material.Shader?.Index ?? 0;
        _materialCutouts[index] = material.AlphaCutoff > 0f;

        var owned = _materialTextures.AsSpan(index * TexturesPerMaterial, TexturesPerMaterial);
        owned[0] = material.BaseColorTexture?.Index ?? -1;
        owned[1] = material.NormalTexture?.Index ?? -1;
        owned[2] = material.SurfaceTexture?.Index ?? -1;
        owned[3] = material.EmissiveTexture?.Index ?? -1;
    }

    public void Dispose()
    {
        foreach (var texture in _textures)
        {
            texture?.Dispose();
        }

        _materials.Dispose();
        _context.Api.DestroyDescriptorPool(_context.Device, _pool, null);
        _context.Api.DestroyDescriptorSetLayout(_context.Device, Layout, null);
        _context.Api.DestroySampler(_context.Device, _sampler, null);
    }

    private static TextureData SolidTexture(byte r, byte g, byte b) => new()
    {
        Width = 1,
        Height = 1,
        Kind = TextureKind.Linear,
        Mips = [[r, g, b, 255]],
    };
}
