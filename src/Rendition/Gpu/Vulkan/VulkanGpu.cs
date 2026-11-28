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
    // What was adopted from outside the layer, which bind groups and the player look up by id.
    private readonly (IdTable<AdoptedView> Views, IdTable<AdoptedBuffer> Buffers) _adopted = (new(), new());
    private readonly IdTable<Sampler> _samplers = new();
    private readonly IdTable<VulkanShader> _shaders = new();
    private readonly IdTable<VulkanPipeline> _pipelines = new();
    private readonly VulkanBindGroups _groups;
    private readonly VulkanPlayer _player;

    public VulkanGpu(VulkanContext context)
    {
        _context = context;
        _groups = new VulkanBindGroups(context, _adopted, _samplers);
        _player = new VulkanPlayer(context.Api, _adopted, _pipelines, _groups);
    }

    /// <summary>
    /// An image view made outside the layer, such as a render target. Its image must rest in
    /// <see cref="ImageLayout.ShaderReadOnlyOptimal"/> between passes, as the layer leaves it. A view only shaders read
    /// can leave out its image and size; drawing to it throws.
    /// </summary>
    public ViewId Adopt(ImageView view, Image image = default, Extent2D extent = default) =>
        new(_adopted.Views.Add(new AdoptedView(view, image, extent)));

    /// <summary>
    /// A view that rests somewhere else, as its flags say: read-only depth, the window's image, or a storage image;
    /// see <see cref="AdoptedView"/>.
    /// </summary>
    public ViewId Adopt(in AdoptedView view) => new(_adopted.Views.Add(view));

    /// <summary>Lets go of an adopted view, whose image stays its maker's to destroy.</summary>
    public void Release(ViewId view) => _adopted.Views.Remove(view.Value);

    /// <summary>A buffer made outside the layer, <paramref name="size"/> bytes long, for bind groups and <see cref="CommandList.ClearBuffer"/>.</summary>
    public BufferId Adopt(Silk.NET.Vulkan.Buffer buffer, long size) => new(_adopted.Buffers.Add(new AdoptedBuffer(buffer, size)));

    /// <summary>Lets go of an adopted buffer, which stays its maker's to destroy.</summary>
    public void Release(BufferId buffer) => _adopted.Buffers.Remove(buffer.Value);

    /// <inheritdoc cref="VulkanBindGroups.Adopt"/>
    public BindGroupId Adopt(DescriptorSet set) => _groups.Adopt(set);

    public override SamplerId CreateSampler(in SamplerDescription description)
    {
        var filter = description.Filter == SamplerFilter.Linear ? Filter.Linear : Filter.Nearest;
        var address = description.Address == SamplerAddress.Repeat ? SamplerAddressMode.Repeat : SamplerAddressMode.ClampToEdge;
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = description.Mipmapped && description.Filter == SamplerFilter.Linear ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest,
            AddressModeU = address,
            AddressModeV = address,
            AddressModeW = address,
            MaxLod = description.Mipmapped ? Vk.LodClampNone : 0f,
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

        var (layout, setLayouts, owned, constantStages) = CreateLayout(shaders, borrowed);
        var targets = description.Targets;
        Span<Format> formats = stackalloc Format[targets.Length];
        Span<PipelineColorBlendAttachmentState> blends = stackalloc PipelineColorBlendAttachmentState[targets.Length];
        VulkanStates.Fill(targets, formats, blends);
        var vertices = description.Vertices;
        Span<VertexInputBindingDescription> bindings = stackalloc VertexInputBindingDescription[vertices.Length];
        Span<VertexInputAttributeDescription> attributes = stackalloc VertexInputAttributeDescription[VulkanStates.AttributeCount(vertices)];
        VulkanStates.Fill(vertices, bindings, attributes);
        var depth = description.Depth;

        // Every state is given, so none of the factory's own choices apply; and so are the override constants,
        // which leaves a shader's defaults alone when there are none, as WebGPU does.
        var pipeline = PipelineFactory.Create(_context, new PipelineDescription
        {
            VertexShader = vertex.Name,
            FragmentShader = shaders.Length > 1 ? shaders[1].Name : null,
            Layout = layout,
            ColorFormats = formats,
            Blends = blends,
            VertexBindings = bindings,
            VertexAttributes = attributes,
            DepthFormat = depth is { } state ? VulkanFormats.Of(state.Format) : Format.Undefined,
            CullMode = VulkanStates.Of(description.Cull),
            Given = new GivenStates(description.Overrides.ToArray(), depth is { } test ? (VulkanStates.Of(test.Compare), test.Write) : null),
        });
        return new PipelineId(_pipelines.Add(new VulkanPipeline(pipeline, layout, setLayouts, owned, shaders, constantStages)));
    }

    public override PipelineId CreateComputePipeline(in ComputePipelineDescription description) => CreateComputePipeline(description, []);

    /// <summary>
    /// A compute pipeline that borrows set layouts as
    /// <see cref="CreateRenderPipeline(in RenderPipelineDescription, ReadOnlySpan{DescriptorSetLayout})"/> does.
    /// </summary>
    public PipelineId CreateComputePipeline(in ComputePipelineDescription description, ReadOnlySpan<DescriptorSetLayout> borrowed)
    {
        var shader = _shaders[description.Shader.Value];
        if (shader.Stage != ShaderStageFlags.ComputeBit)
        {
            throw new ArgumentException($"'{shader.Name}' is not a compute shader.", nameof(description));
        }

        VulkanShader[] shaders = [shader];
        var (layout, setLayouts, owned, constantStages) = CreateLayout(shaders, borrowed);
        var pipeline = PipelineFactory.CreateCompute(_context, shader.Name, layout);
        return new PipelineId(_pipelines.Add(new VulkanPipeline(pipeline, layout, setLayouts, owned, shaders, constantStages)));
    }

    /// <summary>The pipeline layout for <paramref name="shaders"/>: their set layouts, and push constants as big as the biggest block.</summary>
    private (PipelineLayout Layout, DescriptorSetLayout[] SetLayouts, bool[] Owned, ShaderStageFlags ConstantStages) CreateLayout(
        VulkanShader[] shaders, ReadOnlySpan<DescriptorSetLayout> borrowed)
    {
        var setLayouts = _groups.CreateLayouts(shaders, borrowed, out bool[] owned);
        var constantStages = ShaderStageFlags.None;
        int constantsSize = 0;
        foreach (var shader in shaders.Where(shader => shader.Layout.ConstantsSize > 0))
        {
            constantStages |= shader.Stage;
            constantsSize = Math.Max(constantsSize, shader.Layout.ConstantsSize);
        }

        PushConstantRange? range = constantsSize > 0 ? new PushConstantRange(constantStages, 0, (uint)constantsSize) : null;
        return (_context.CreatePipelineLayout(setLayouts, range), setLayouts, owned, constantStages);
    }

    /// <summary>
    /// A graphics pipeline the renderer built itself, such as a scene pass's from a surface shader's code or a
    /// multiview capture's, for command lists to draw with. The layer takes the pipeline and destroys it with
    /// <see cref="Destroy(PipelineId)"/>; <paramref name="layout"/> and <paramref name="setLayouts"/> stay lent. Constants
    /// go to <paramref name="constantStages"/> from offset 0, and live reload still swaps the pipeline under the id.
    /// </summary>
    public PipelineId Adopt(LivePipeline pipeline, PipelineLayout layout, DescriptorSetLayout[] setLayouts, ShaderStageFlags constantStages) =>
        new(_pipelines.Add(new VulkanPipeline(pipeline, layout, setLayouts, new bool[setLayouts.Length], [], constantStages) { LayoutLent = true }));

    /// <summary>
    /// The Vulkan pipeline <paramref name="pipeline"/> is now, which changes when its shaders are reloaded live, for a
    /// pass that bakes only when what it bakes with changes.
    /// </summary>
    public Pipeline Current(PipelineId pipeline) => _pipelines[pipeline.Value].Pipeline.Handle;

    public override BindGroupId CreateBindGroup(PipelineId pipeline, int group, ReadOnlySpan<Bind> binds) =>
        _groups.Create(_pipelines[pipeline.Value], group, binds);

    public override void Destroy(PipelineId pipeline)
    {
        var entry = _pipelines[pipeline.Value];
        _pipelines.Remove(pipeline.Value);
        entry.Destroy(_context);
    }

    public override void Destroy(BindGroupId bindGroup) => _groups.Destroy(bindGroup);

    /// <summary>Records <paramref name="commands"/> into <paramref name="into"/>, which the caller submits.</summary>
    public void Play(CommandList commands, CommandBuffer into) => _player.Play(commands, into);

    public override void Dispose()
    {
        _groups.Dispose();
        foreach (var pipeline in _pipelines.Items)
        {
            pipeline.Destroy(_context);
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

    private static readonly Dictionary<Format, GpuFormat> Back = Formats.ToDictionary(pair => pair.Value, pair => pair.Key);

    public static Format Of(GpuFormat format) => Formats[format];

    /// <summary>The layer's format for a Vulkan one, such as the window's, made outside the layer.</summary>
    public static GpuFormat ToGpu(Format format) =>
        Back.TryGetValue(format, out var gpu) ? gpu : throw new NotSupportedException($"The GPU layer has no format for {format}.");
}
