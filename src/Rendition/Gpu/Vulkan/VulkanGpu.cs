using Rendition.Vulkan;
using Silk.NET.Vulkan;

namespace Rendition.Gpu.Vulkan;

/// <summary>
/// The GPU layer on Vulkan. Until every pass has moved to the layer, passes that have and passes that haven't share
/// a frame: <see cref="Adopt(ImageView, Image, Extent2D)"/> and <see cref="Adopt(DescriptorSet)"/> let a layer pass
/// use what an old pass made, and <see cref="Play"/> records a layer pass into the frame's command buffer.
/// </summary>
internal sealed unsafe class VulkanGpu : GpuDevice
{
    private readonly VulkanContext _context;
    private readonly IdTable<AdoptedView> _views = new();
    private readonly IdTable<Sampler> _samplers = new();
    private readonly IdTable<VulkanShader> _shaders = new();
    private readonly IdTable<VulkanPipeline> _pipelines = new();
    private readonly VulkanBindGroups _groups;
    private readonly VulkanPlayer _player;

    public VulkanGpu(VulkanContext context)
    {
        _context = context;
        _groups = new VulkanBindGroups(context, _views, _samplers);
        _player = new VulkanPlayer(context.Api, _views, _pipelines, _groups);
    }

    /// <summary>
    /// An image view made outside the layer, such as a render target. Its image must rest in
    /// <see cref="ImageLayout.ShaderReadOnlyOptimal"/> between passes, as the layer leaves it.
    /// </summary>
    public ViewId Adopt(ImageView view, Image image, Extent2D extent) => new(_views.Add(new AdoptedView(view, image, extent)));

    /// <inheritdoc cref="VulkanBindGroups.Adopt"/>
    public BindGroupId Adopt(DescriptorSet set) => _groups.Adopt(set);

    public override SamplerId CreateSampler(in SamplerDescription description)
    {
        var filter = description.Filter == SamplerFilter.Linear ? Filter.Linear : Filter.Nearest;
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        _context.Api.CreateSampler(_context.Device, &info, null, out var sampler).Check();
        return new SamplerId(_samplers.Add(sampler));
    }

    public override ShaderId CreateShader(string name) => new(_shaders.Add(VulkanShader.Load(name)));

    public override PipelineId CreateRenderPipeline(in RenderPipelineDescription description) => CreateRenderPipeline(description, []);

    /// <summary>
    /// A pipeline that uses <paramref name="borrowed"/>'s layout for each group it holds one for, so a set made outside
    /// the layer with that layout can be bound through <see cref="Adopt(DescriptorSet)"/>. A default entry leaves the
    /// group to be laid out from the shaders.
    /// </summary>
    public PipelineId CreateRenderPipeline(in RenderPipelineDescription description, ReadOnlySpan<DescriptorSetLayout> borrowed)
    {
        var vertex = _shaders[description.VertexShader.Value];
        VulkanShader[] shaders = description.FragmentShader.Value == 0
            ? [vertex]
            : [vertex, _shaders[description.FragmentShader.Value]];

        var setLayouts = _groups.CreateLayouts(shaders, borrowed, out bool[] owned);
        var constantStages = ShaderStageFlags.None;
        int constantsSize = 0;
        foreach (var shader in shaders.Where(shader => shader.Layout.ConstantsSize > 0))
        {
            constantStages |= shader.Stage;
            constantsSize = Math.Max(constantsSize, shader.Layout.ConstantsSize);
        }

        PushConstantRange? range = constantsSize > 0 ? new PushConstantRange(constantStages, 0, (uint)constantsSize) : null;
        var layout = _context.CreatePipelineLayout(setLayouts, range);
        Span<Format> formats = stackalloc Format[description.ColorFormats.Length];
        for (int i = 0; i < formats.Length; i++)
        {
            formats[i] = VulkanFormats.Of(description.ColorFormats[i]);
        }

        var pipeline = PipelineFactory.Create(_context, new PipelineDescription
        {
            VertexShader = vertex.Name,
            FragmentShader = shaders.Length > 1 ? shaders[1].Name : null,
            Layout = layout,
            ColorFormats = formats,
            DepthFormat = Format.Undefined,
            Transparent = description.Blend,
        });
        return new PipelineId(_pipelines.Add(new VulkanPipeline(pipeline, layout, setLayouts, owned, shaders, constantStages)));
    }

    public override BindGroupId CreateBindGroup(PipelineId pipeline, int group, ReadOnlySpan<Bind> binds) =>
        _groups.Create(_pipelines[pipeline.Value], group, binds);

    public override void Destroy(PipelineId pipeline)
    {
        var entry = _pipelines[pipeline.Value];
        _pipelines.Remove(pipeline.Value);
        DestroyPipeline(entry);
    }

    public override void Destroy(BindGroupId bindGroup) => _groups.Destroy(bindGroup);

    /// <summary>Records <paramref name="commands"/> into <paramref name="into"/>, which the caller submits.</summary>
    public void Play(CommandList commands, CommandBuffer into) => _player.Play(commands, into);

    private void DestroyPipeline(VulkanPipeline pipeline)
    {
        pipeline.Pipeline.Destroy();
        _context.Api.DestroyPipelineLayout(_context.Device, pipeline.Layout, null);
        for (int group = 0; group < pipeline.SetLayouts.Length; group++)
        {
            if (pipeline.Owned[group])
            {
                _context.Api.DestroyDescriptorSetLayout(_context.Device, pipeline.SetLayouts[group], null);
            }
        }
    }

    public override void Dispose()
    {
        _groups.Dispose();
        foreach (var pipeline in _pipelines.Items)
        {
            DestroyPipeline(pipeline);
        }

        foreach (var sampler in _samplers.Items)
        {
            _context.Api.DestroySampler(_context.Device, sampler, null);
        }
    }
}

/// <summary>The Vulkan format for each of the layer's formats.</summary>
internal static class VulkanFormats
{
    private static readonly Dictionary<GpuFormat, Format> Formats = new()
    {
        [GpuFormat.None] = Format.Undefined,
        [GpuFormat.R8Unorm] = Format.R8Unorm,
        [GpuFormat.R32Float] = Format.R32Sfloat,
        [GpuFormat.Rg16Float] = Format.R16G16Sfloat,
        [GpuFormat.Rgba8Unorm] = Format.R8G8B8A8Unorm,
        [GpuFormat.Rgba8UnormSrgb] = Format.R8G8B8A8Srgb,
        [GpuFormat.Bgra8UnormSrgb] = Format.B8G8R8A8Srgb,
        [GpuFormat.Rg11b10Ufloat] = Format.B10G11R11UfloatPack32,
        [GpuFormat.Rgba16Float] = Format.R16G16B16A16Sfloat,
        [GpuFormat.Rgba32Float] = Format.R32G32B32A32Sfloat,
        [GpuFormat.Depth32Float] = Format.D32Sfloat,
        [GpuFormat.Bc4RUnorm] = Format.BC4UnormBlock,
        [GpuFormat.Bc5RgUnorm] = Format.BC5UnormBlock,
        [GpuFormat.Bc6hRgbUfloat] = Format.BC6HUfloatBlock,
        [GpuFormat.Bc7RgbaUnorm] = Format.BC7UnormBlock,
        [GpuFormat.Bc7RgbaUnormSrgb] = Format.BC7SrgbBlock,
    };

    public static Format Of(GpuFormat format) => Formats[format];
}
