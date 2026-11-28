using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Vulkan;

/// <summary>One compiled shader and the bindings it declares.</summary>
internal sealed record VulkanShader(string Name, ShaderLayout Layout, ShaderStageFlags Stage)
{
    public static VulkanShader Load(string name)
    {
        var stage = Path.GetExtension(name) switch
        {
            ".vert" => ShaderStageFlags.VertexBit,
            ".frag" => ShaderStageFlags.FragmentBit,
            ".comp" => ShaderStageFlags.ComputeBit,
            _ => throw new ArgumentException($"'{name}' is not a .vert, .frag or .comp shader.", nameof(name)),
        };
        string path = Path.Combine(AppContext.BaseDirectory, "shaders", name + ".layout.spv");
        return new VulkanShader(name, ShaderLayout.FromSpirv(File.ReadAllBytes(path)), stage);
    }
}

/// <summary>
/// A pipeline with the set layouts it was made with. <see cref="Owned"/> says which of them the GPU layer laid out
/// from the shaders; the rest were lent by a pass that hasn't moved to the layer, and stay that pass's to destroy.
/// </summary>
internal sealed record VulkanPipeline(
    LivePipeline Pipeline, PipelineLayout Layout, DescriptorSetLayout[] SetLayouts, bool[] Owned, VulkanShader[] Shaders,
    ShaderStageFlags ConstantStages)
{
    /// <summary>The shader errors name: the fragment shader when there is one.</summary>
    public string Name => Shaders[^1].Name;

    public PipelineBindPoint BindPoint =>
        Shaders[0].Stage == ShaderStageFlags.ComputeBit ? PipelineBindPoint.Compute : PipelineBindPoint.Graphics;

    /// <summary>Destroys the pipeline, its layout and the set layouts the layer made for it, but not borrowed ones.</summary>
    public unsafe void Destroy(VulkanContext context)
    {
        Pipeline.Destroy();
        context.Api.DestroyPipelineLayout(context.Device, Layout, null);
        for (int group = 0; group < SetLayouts.Length; group++)
        {
            if (Owned[group])
            {
                context.Api.DestroyDescriptorSetLayout(context.Device, SetLayouts[group], null);
            }
        }
    }
}

/// <summary>
/// An image view made outside the layer, with what the layer needs to move its image between layouts. A
/// <see cref="ReadOnlyDepth"/> view is depth that rests in <see cref="ImageLayout.DepthReadOnlyOptimal"/> and is only sampled.
/// A <see cref="Surface"/> is drawn to in the layout its caller leaves it in, as the window's image is.
/// A <see cref="Storage"/> image is one compute shaders write; it rests in <see cref="ImageLayout.General"/> between passes
/// so it can be written and read without moving, and render passes can't draw to it.
/// </summary>
internal readonly record struct AdoptedView(
    ImageView View, Image Image, Extent2D Extent, bool ReadOnlyDepth = false, bool Surface = false, bool Storage = false);

/// <summary>A buffer made outside the layer, and how many bytes of it bind groups and clears see.</summary>
internal readonly record struct AdoptedBuffer(Silk.NET.Vulkan.Buffer Buffer, long Size);

/// <summary>A descriptor set, and the pool it came from when the layer made it. An adopted set has no pool.</summary>
internal readonly record struct VulkanBindGroup(DescriptorSet Set, DescriptorPool Pool);

/// <summary>
/// Lays out descriptor sets from what the shaders declare, and fills bind groups by binding name. Each bind group
/// gets a pool of its own, sized to it, so destroying one frees it at once.
/// </summary>
internal sealed unsafe class VulkanBindGroups(
    VulkanContext context, (IdTable<AdoptedView> Views, IdTable<AdoptedBuffer> Buffers) resources, IdTable<Sampler> samplers) : IDisposable
{
    private static readonly Dictionary<ResourceKind, DescriptorType> Types = new()
    {
        [ResourceKind.UniformBuffer] = DescriptorType.UniformBuffer,
        [ResourceKind.StorageBuffer] = DescriptorType.StorageBuffer,
        [ResourceKind.SampledImage] = DescriptorType.SampledImage,
        [ResourceKind.StorageImage] = DescriptorType.StorageImage,
        [ResourceKind.Sampler] = DescriptorType.Sampler,
        [ResourceKind.ImageWithSampler] = DescriptorType.CombinedImageSampler,
        [ResourceKind.Structure] = DescriptorType.AccelerationStructureKhr,
    };

    private readonly IdTable<VulkanBindGroup> _groups = new();
    private readonly Dictionary<ulong, BindGroupId> _adopted = [];

    public DescriptorSet this[BindGroupId group] => _groups[group.Value].Set;

    /// <summary>
    /// One set layout per group the shaders use, from 0 to the highest. Where <paramref name="borrowed"/> holds a
    /// layout for a group, that one is used as it is.
    /// </summary>
    public DescriptorSetLayout[] CreateLayouts(VulkanShader[] shaders, ReadOnlySpan<DescriptorSetLayout> borrowed, out bool[] owned)
    {
        int count = borrowed.Length;
        foreach (var shader in shaders)
        {
            foreach (var named in shader.Layout.Bindings)
            {
                count = Math.Max(count, named.Binding.Group + 1);
            }
        }

        var layouts = new DescriptorSetLayout[count];
        owned = new bool[count];
        for (int group = 0; group < count; group++)
        {
            owned[group] = group >= borrowed.Length || borrowed[group].Handle == 0;
            layouts[group] = owned[group] ? CreateLayout(shaders, group) : borrowed[group];
        }

        return layouts;
    }

    private DescriptorSetLayout CreateLayout(VulkanShader[] shaders, int group)
    {
        var bindings = new SortedDictionary<int, DescriptorSetLayoutBinding>();
        foreach (var shader in shaders)
        {
            foreach (var (name, binding) in shader.Layout.Bindings.Where(named => named.Binding.Group == group))
            {
                if (binding.Count == 0)
                {
                    throw new NotSupportedException($"'{name}' in '{shader.Name}' is an array without a size, which the GPU layer can't bind yet.");
                }

                var entry = bindings.GetValueOrDefault(binding.Binding,
                    new DescriptorSetLayoutBinding((uint)binding.Binding, Types[binding.Kind], (uint)binding.Count, ShaderStageFlags.None));
                entry.StageFlags |= shader.Stage;
                bindings[binding.Binding] = entry;
            }
        }

        return context.CreateSetLayout(bindings.Values.ToArray());
    }

    public BindGroupId Create(VulkanPipeline pipeline, int group, ReadOnlySpan<Bind> binds)
    {
        if (group < 0 || group >= pipeline.SetLayouts.Length || !pipeline.Owned[group])
        {
            throw new ArgumentOutOfRangeException(nameof(group), $"Group {group} of '{pipeline.Name}' is not one the GPU layer laid out.");
        }

        var found = new ShaderBinding[binds.Length];
        var sizes = new DescriptorPoolSize[binds.Length];
        for (int i = 0; i < binds.Length; i++)
        {
            found[i] = Find(pipeline, binds[i].Name, group);
            sizes[i] = new DescriptorPoolSize(Types[found[i].Kind], 1);
        }

        var pool = context.CreateDescriptorPool(1, sizes);
        var set = context.AllocateSet(pool, pipeline.SetLayouts[group]);
        var images = stackalloc DescriptorImageInfo[binds.Length];
        var buffers = stackalloc DescriptorBufferInfo[binds.Length];
        var writes = stackalloc WriteDescriptorSet[binds.Length];
        for (int i = 0; i < binds.Length; i++)
        {
            var type = Types[found[i].Kind];
            if (found[i].Kind is ResourceKind.UniformBuffer or ResourceKind.StorageBuffer)
            {
                var buffer = resources.Buffers[BufferOf(binds[i], pipeline).Value];
                buffers[i] = new DescriptorBufferInfo(buffer.Buffer, 0, (ulong)buffer.Size);
                writes[i] = Descriptors.BufferWrite(set, (uint)found[i].Binding, &buffers[i], type);
            }
            else
            {
                var view = resources.Views[ViewOf(binds[i], pipeline).Value];
                var sampler = binds[i].Sampler.Value == 0 ? default : samplers[binds[i].Sampler.Value];
                images[i] = new DescriptorImageInfo(sampler, view.View, RestingLayout(view));
                writes[i] = Descriptors.ImageWrite(set, (uint)found[i].Binding, type, &images[i]);
            }
        }

        context.Api.UpdateDescriptorSets(context.Device, (uint)binds.Length, writes, 0, null);
        return new BindGroupId(_groups.Add(new VulkanBindGroup(set, pool)));
    }

    /// <summary>The layout an adopted view's image rests in, which its descriptors say it is in.</summary>
    private static ImageLayout RestingLayout(AdoptedView view) => view switch
    {
        { ReadOnlyDepth: true } => ImageLayout.DepthReadOnlyOptimal,
        { Storage: true } => ImageLayout.General,
        _ => ImageLayout.ShaderReadOnlyOptimal,
    };

    private static BufferId BufferOf(in Bind bind, VulkanPipeline pipeline) => bind.BufferId.Value != 0
        ? bind.BufferId
        : throw new ArgumentException($"'{bind.Name}' in '{pipeline.Name}' is a buffer; bind it with Bind.Buffer.");

    private static ViewId ViewOf(in Bind bind, VulkanPipeline pipeline) => bind.View.Value != 0
        ? bind.View
        : throw new ArgumentException($"'{bind.Name}' in '{pipeline.Name}' is an image; bind it with Bind.Image or Bind.StorageImage.");

    private static ShaderBinding Find(VulkanPipeline pipeline, string name, int group)
    {
        foreach (var shader in pipeline.Shaders)
        {
            if (!shader.Layout.TryFind(name, out var binding))
            {
                continue;
            }

            if (binding.Group != group)
            {
                throw new ArgumentException($"Shader '{pipeline.Name}' has '{name}' in group {binding.Group}, not group {group}.");
            }

            return binding;
        }

        throw new ArgumentException($"Shader '{pipeline.Name}' has no binding named '{name}'.");
    }

    /// <summary>
    /// A set made outside the layer, for a layer pass to bind beside its own until every pass has moved. Adopting the
    /// same set again gives the same id, so a pass can adopt the frame's sets every frame without the table growing.
    /// </summary>
    public BindGroupId Adopt(DescriptorSet set)
    {
        if (!_adopted.TryGetValue(set.Handle, out var id))
        {
            id = new BindGroupId(_groups.Add(new VulkanBindGroup(set, default)));
            _adopted[set.Handle] = id;
        }

        return id;
    }

    public void Destroy(BindGroupId group)
    {
        var entry = _groups[group.Value];
        _groups.Remove(group.Value);
        _adopted.Remove(entry.Set.Handle);
        DestroyPool(entry);
    }

    private void DestroyPool(VulkanBindGroup group)
    {
        if (group.Pool.Handle != 0)
        {
            context.Api.DestroyDescriptorPool(context.Device, group.Pool, null);
        }
    }

    public void Dispose()
    {
        foreach (var group in _groups.Items)
        {
            DestroyPool(group);
        }
    }
}
