using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>What differs between the renderer's graphics pipelines.</summary>
internal ref struct PipelineDescription
{
    public string VertexShader;

    /// <summary>Null for a pass that only writes depth, which lets the GPU skip shading.</summary>
    public string? FragmentShader;

    /// <summary>
    /// Compiled code to use instead of <see cref="FragmentShader"/>, and the file it came from, as shaders are named,
    /// such as <c>terrain.frag</c>, when it has one. Live shader reload reads the code from there again when that
    /// file changes.
    /// </summary>
    public (byte[] Bytes, string? Name)? FragmentCode;
    public PipelineLayout Layout;
    public ReadOnlySpan<Format> ColorFormats;
    public Format DepthFormat;
    public ReadOnlySpan<VertexInputBindingDescription> VertexBindings;
    public ReadOnlySpan<VertexInputAttributeDescription> VertexAttributes;
    public bool TwoSided;

    /// <summary>Blends premultiplied colour and leaves depth alone.</summary>
    public bool Transparent;

    /// <summary>
    /// Runs inside a pass with a depth attachment but neither tests nor writes it, nor
    /// culls, for camera-facing squares that read depth themselves.
    /// </summary>
    public bool IgnoreDepth;

    /// <summary>One blend state per colour target, in place of the ones <see cref="Transparent"/> picks.</summary>
    public ReadOnlySpan<PipelineColorBlendAttachmentState> Blends;

    /// <summary>Which faces to leave out, in place of the usual choice.</summary>
    public CullModeFlags? CullMode;

    /// <summary>Which layers one draw renders to at once, through multiview, with 0 for a single one.</summary>
    public uint ViewMask;

    /// <summary>Samples a pixel of the targets has. Left unset, one.</summary>
    public SampleCountFlags Samples;

    /// <summary>
    /// Counts a pixel as covered when any part of a triangle touches it, by VK_EXT_conservative_rasterization's
    /// overestimation. Only where <see cref="DeviceAbilities.ConservativeRasterization"/> is true.
    /// </summary>
    public bool Conservative;

    /// <summary>
    /// Builds a surface shader for materials without holes: its CUT_OUTS specialization constant (constant_id 0, in
    /// surface.glsl) is false, so it never discards and the GPU can reject hidden pixels before shading them.
    /// </summary>
    public bool Solid;

    /// <summary>
    /// For surfaces whose depth is already in the depth image: draws only the pixels at exactly that depth, and
    /// writes no depth of its own. So each pixel is shaded once, by the surface that shows.
    /// </summary>
    public bool DepthDrawn;

    /// <summary>Whether what the pipeline draws goes into the depth image, when there is one.</summary>
    public readonly bool WritesDepth => !Transparent && !DepthDrawn;

    /// <summary>What a pixel's depth must be to the depth already there. Reversed-Z: nearer surfaces have larger depth.</summary>
    public readonly CompareOp DepthCompare => DepthDrawn ? CompareOp.Equal : CompareOp.Greater;
}

/// <summary>
/// A pipeline, used wherever its handle is. With <see cref="RendererOptions.LiveShaders"/> it also remembers how it
/// was made, so <see cref="PipelineFactory.Reload"/> can build it again from changed shaders and swap the handle.
/// So keep this object, never a copy of <see cref="Handle"/>, and end it with <see cref="Destroy"/>. Its owner is the
/// device it was made on and the set of live pipelines it leaves when destroyed, null without live shaders.
/// </summary>
internal sealed class LivePipeline(
    (Vk Api, Device Device, HashSet<LivePipeline>? Live) owner,
    Pipeline handle,
    Func<Pipeline>? rebuild,
    string[] shaders)
{
    public Pipeline Handle { get; private set; } = handle;

    public static implicit operator Pipeline(LivePipeline pipeline) => pipeline.Handle;

    public bool Uses(IReadOnlySet<string> changed) => shaders.Any(changed.Contains);

    /// <summary>Builds the pipeline again and returns the old handle, which the caller destroys once the GPU is idle.</summary>
    public Pipeline Rebuild()
    {
        var old = Handle;
        Handle = rebuild!();
        return old;
    }

    public unsafe void Destroy()
    {
        owner.Live?.Remove(this);
        owner.Api.DestroyPipeline(owner.Device, Handle, null);
        Handle = default;
    }
}

internal static unsafe class PipelineFactory
{
    private static readonly PipelineInputAssemblyStateCreateInfo TriangleList = new()
    {
        SType = StructureType.PipelineInputAssemblyStateCreateInfo,
        Topology = PrimitiveTopology.TriangleList,
    };

    // One viewport and scissor, both set while drawing.
    private static readonly PipelineViewportStateCreateInfo OneViewport = new()
    {
        SType = StructureType.PipelineViewportStateCreateInfo,
        ViewportCount = 1,
        ScissorCount = 1,
    };

    public static LivePipeline Create(VulkanContext context, in PipelineDescription description)
    {
        var handle = Build(context, description);
        if (context.LivePipelines is not { } live)
        {
            return new LivePipeline(OwnerOf(context), handle, null, []);
        }

        var kept = Keep(description);
        string? fragment = description.FragmentCode is { } code ? code.Name : description.FragmentShader;
        string[] shaders = fragment is null ? [description.VertexShader] : [description.VertexShader, fragment];
        var pipeline = new LivePipeline(OwnerOf(context), handle, () => Build(context, kept.Restore()), shaders);
        live.Add(pipeline);
        return pipeline;
    }

    // The description's spans don't outlive the call, so what they hold is copied for the next build.
    private static KeptDescription Keep(in PipelineDescription description) => new()
    {
        VertexShader = description.VertexShader,
        FragmentShader = description.FragmentShader,
        FragmentCode = description.FragmentCode,
        Layout = description.Layout,
        ColorFormats = description.ColorFormats.ToArray(),
        DepthFormat = description.DepthFormat,
        VertexBindings = description.VertexBindings.ToArray(),
        VertexAttributes = description.VertexAttributes.ToArray(),
        Blends = description.Blends.ToArray(),
        TwoSided = description.TwoSided,
        Transparent = description.Transparent,
        IgnoreDepth = description.IgnoreDepth,
        CullMode = description.CullMode,
        ViewMask = description.ViewMask,
        Samples = description.Samples,
        Conservative = description.Conservative,
        Solid = description.Solid,
        DepthDrawn = description.DepthDrawn,
    };

    /// <summary>A <see cref="PipelineDescription"/> that can be kept, to build its pipeline again.</summary>
    private sealed class KeptDescription
    {
        public required string VertexShader;
        public string? FragmentShader;
        public (byte[] Bytes, string? Name)? FragmentCode;
        public PipelineLayout Layout;
        public required Format[] ColorFormats;
        public Format DepthFormat;
        public required VertexInputBindingDescription[] VertexBindings;
        public required VertexInputAttributeDescription[] VertexAttributes;
        public required PipelineColorBlendAttachmentState[] Blends;
        public bool TwoSided;
        public bool Transparent;
        public bool IgnoreDepth;
        public CullModeFlags? CullMode;
        public uint ViewMask;
        public SampleCountFlags Samples;
        public bool Conservative;
        public bool Solid;
        public bool DepthDrawn;

        /// <summary>The description again. Code that has a file is read from it as it is now.</summary>
        public PipelineDescription Restore() => new()
        {
            VertexShader = VertexShader,
            FragmentShader = FragmentShader,
            FragmentCode = FragmentCode is (_, { } name) ? (ReadShader(name), name) : FragmentCode,
            Layout = Layout,
            ColorFormats = ColorFormats,
            DepthFormat = DepthFormat,
            VertexBindings = VertexBindings,
            VertexAttributes = VertexAttributes,
            Blends = Blends,
            TwoSided = TwoSided,
            Transparent = Transparent,
            IgnoreDepth = IgnoreDepth,
            CullMode = CullMode,
            ViewMask = ViewMask,
            Samples = Samples,
            Conservative = Conservative,
            Solid = Solid,
            DepthDrawn = DepthDrawn,
        };
    }

    /// <summary>
    /// Builds every pipeline that uses one of these shaders again, from the compiled files as they are now, and
    /// returns how many it built. Names are as shaders are named, such as <c>lighting.frag</c>. Waits for the GPU
    /// to finish with the old pipelines, so call it between frames. Does nothing without
    /// <see cref="RendererOptions.LiveShaders"/>.
    /// </summary>
    public static int Reload(VulkanContext context, IReadOnlySet<string> changed)
    {
        if (context.LivePipelines is not { } live)
        {
            return 0;
        }

        // A build that throws leaves that pipeline and the ones after it as they were, and the ones before it new.
        var old = new List<Pipeline>();
        try
        {
            foreach (var pipeline in live)
            {
                if (pipeline.Uses(changed))
                {
                    old.Add(pipeline.Rebuild());
                }
            }
        }
        finally
        {
            if (old.Count > 0)
            {
                context.WaitIdle();
                foreach (var pipeline in old)
                {
                    context.Api.DestroyPipeline(context.Device, pipeline, null);
                }
            }
        }

        return old.Count;
    }

    private static Pipeline Build(VulkanContext context, in PipelineDescription description)
    {
        var api = context.Api;
        var vertexModule = LoadShader(context, description.VertexShader);
        var fragmentModule = LoadFragment(context, description);
        bool hasFragment = fragmentModule.Handle != 0;
        nint entryPoint = SilkMarshal.StringToPtr("main");

        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = ShaderStep(ShaderStageFlags.VertexBit, vertexModule, entryPoint);
        // Always given: for a shader without the constant it changes nothing.
        uint cutOuts = Convert.ToUInt32(!description.Solid);
        var cutOutsEntry = new SpecializationMapEntry(0, 0, sizeof(uint));
        var specialization = new SpecializationInfo
        {
            MapEntryCount = 1,
            PMapEntries = &cutOutsEntry,
            DataSize = sizeof(uint),
            PData = &cutOuts,
        };
        stages[1] = ShaderStep(ShaderStageFlags.FragmentBit, fragmentModule, entryPoint);
        stages[1].PSpecializationInfo = &specialization;

        var pipeline = CreateGraphics(context, description, stages, hasFragment ? 2u : 1u);

        SilkMarshal.Free(entryPoint);
        api.DestroyShaderModule(context.Device, vertexModule, null);
        if (hasFragment)
        {
            api.DestroyShaderModule(context.Device, fragmentModule, null);
        }

        return pipeline;
    }

    // No module (a null handle) for a pass that only writes depth.
    private static ShaderModule LoadFragment(VulkanContext context, in PipelineDescription description)
    {
        if (description.FragmentCode is { } code)
        {
            return CreateModule(context, code.Bytes);
        }

        if (description.FragmentShader is { } fragmentShader)
        {
            return LoadShader(context, fragmentShader);
        }

        return default;
    }

    // Made through the constructor, whose stage argument doesn't read as the name of a type to Roe.
    private static PipelineShaderStageCreateInfo ShaderStep(ShaderStageFlags stage, ShaderModule module, nint entryPoint) =>
        new(stage: stage, module: module, pName: (byte*)entryPoint);

    // How the description's triangles become pixels, and how those pixels land in the depth and colour targets.
    private static Pipeline CreateGraphics(
        VulkanContext context, in PipelineDescription description, PipelineShaderStageCreateInfo* stages, uint stageCount)
    {
        bool hasDepth = description.DepthFormat != Format.Undefined && !description.IgnoreDepth;
        var conservative = new PipelineRasterizationConservativeStateCreateInfoEXT
        {
            SType = StructureType.PipelineRasterizationConservativeStateCreateInfoExt,
            ConservativeRasterizationMode = ConservativeRasterizationModeEXT.OverestimateExt,
        };
        var rasterizer = Rasterizer(context, description, hasDepth, &conservative);
        var multisample = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = description.Samples == 0 ? SampleCountFlags.Count1Bit : description.Samples,
        };
        var depthStencil = DepthStencil(description, hasDepth);

        int colorCount = description.ColorFormats.Length;
        var blendAttachments = stackalloc PipelineColorBlendAttachmentState[colorCount];
        FillBlends(description, new Span<PipelineColorBlendAttachmentState>(blendAttachments, colorCount));
        var blend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = (uint)colorCount,
            PAttachments = blendAttachments,
        };

        var createInfo = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = stageCount,
            PStages = stages,
            PRasterizationState = &rasterizer,
            PMultisampleState = &multisample,
            PDepthStencilState = &depthStencil,
            PColorBlendState = &blend,
            Layout = description.Layout,
        };
        return CreateWithSharedState(context, description, createInfo);
    }

    private static PipelineRasterizationStateCreateInfo Rasterizer(
        VulkanContext context,
        in PipelineDescription description,
        bool hasDepth,
        PipelineRasterizationConservativeStateCreateInfoEXT* conservative)
    {
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = description.CullMode
                ?? (hasDepth && !description.TwoSided ? CullModeFlags.BackBit : CullModeFlags.None),
            FrontFace = FrontFace.CounterClockwise,
            LineWidth = 1f,
        };
        if (description.Conservative)
        {
            if (!context.Abilities.ConservativeRasterization)
            {
                throw new InvalidOperationException("Conservative rasterization needs VK_EXT_conservative_rasterization, which this device lacks.");
            }

            rasterizer.PNext = conservative;
        }

        return rasterizer;
    }

    // The description's vertex layout. The caller pins its spans.
    private static PipelineVertexInputStateCreateInfo VertexInput(
        in PipelineDescription description,
        VertexInputBindingDescription* bindings,
        VertexInputAttributeDescription* attributes) => new()
    {
        SType = StructureType.PipelineVertexInputStateCreateInfo,
        VertexBindingDescriptionCount = (uint)description.VertexBindings.Length,
        PVertexBindingDescriptions = bindings,
        VertexAttributeDescriptionCount = (uint)description.VertexAttributes.Length,
        PVertexAttributeDescriptions = attributes,
    };

    private static PipelineDepthStencilStateCreateInfo DepthStencil(in PipelineDescription description, bool hasDepth) => new()
    {
        SType = StructureType.PipelineDepthStencilStateCreateInfo,
        DepthTestEnable = hasDepth,
        DepthWriteEnable = hasDepth && description.WritesDepth,
        DepthCompareOp = description.DepthCompare,
    };

    // The description's own blend states, or else premultiplied blending when it is transparent and none when not.
    private static void FillBlends(in PipelineDescription description, Span<PipelineColorBlendAttachmentState> blends)
    {
        for (int i = 0; i < blends.Length; i++)
        {
            if (!description.Blends.IsEmpty)
            {
                blends[i] = description.Blends[i];
                continue;
            }

            blends[i] = new PipelineColorBlendAttachmentState
            {
                BlendEnable = description.Transparent,
                SrcColorBlendFactor = BlendFactor.One,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit
                    | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
        }
    }

    // Adds what every graphics pipeline shares (triangle lists, one viewport and scissor set while drawing) and the
    // description's vertex layout and target formats, then creates the pipeline.
    private static Pipeline CreateWithSharedState(
        VulkanContext context, in PipelineDescription description, GraphicsPipelineCreateInfo createInfo)
    {
        var inputAssembly = TriangleList;
        var viewport = OneViewport;
        var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates,
        };
        createInfo.PInputAssemblyState = &inputAssembly;
        createInfo.PViewportState = &viewport;
        createInfo.PDynamicState = &dynamicState;

        Pipeline pipeline;
        fixed (Format* colorFormats = description.ColorFormats)
        fixed (VertexInputBindingDescription* bindings = description.VertexBindings)
        fixed (VertexInputAttributeDescription* attributes = description.VertexAttributes)
        {
            var vertexInput = VertexInput(description, bindings, attributes);
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ViewMask = description.ViewMask,
                ColorAttachmentCount = (uint)description.ColorFormats.Length,
                PColorAttachmentFormats = colorFormats,
                DepthAttachmentFormat = description.DepthFormat,
            };
            createInfo.PNext = &rendering;
            createInfo.PVertexInputState = &vertexInput;
            context.Api.CreateGraphicsPipelines(context.Device, default, 1, &createInfo, null, &pipeline).Check();
        }

        return pipeline;
    }

    public static LivePipeline CreateCompute(VulkanContext context, string shader, PipelineLayout layout)
    {
        var handle = BuildCompute(context, shader, layout);
        if (context.LivePipelines is not { } live)
        {
            return new LivePipeline(OwnerOf(context), handle, null, []);
        }

        var pipeline = new LivePipeline(OwnerOf(context), handle, () => BuildCompute(context, shader, layout), [shader]);
        live.Add(pipeline);
        return pipeline;
    }

    private static (Vk Api, Device Device, HashSet<LivePipeline>? Live) OwnerOf(VulkanContext context) =>
        (context.Api, context.Device, context.LivePipelines);

    private static Pipeline BuildCompute(VulkanContext context, string shader, PipelineLayout layout)
    {
        var api = context.Api;
        var module = LoadShader(context, shader);
        nint entryPoint = SilkMarshal.StringToPtr("main");
        // Made through the constructor, whose stage argument doesn't read as the name of a type to Roe.
        var createInfo = new ComputePipelineCreateInfo(
            stage: ShaderStep(ShaderStageFlags.ComputeBit, module, entryPoint),
            layout: layout);

        Pipeline pipeline;
        api.CreateComputePipelines(context.Device, default, 1, &createInfo, null, &pipeline).Check();
        SilkMarshal.Free(entryPoint);
        api.DestroyShaderModule(context.Device, module, null);
        return pipeline;
    }

    private static ShaderModule LoadShader(VulkanContext context, string name) => CreateModule(context, ReadShader(name));

    private static byte[] ReadShader(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "shaders", name + ".spv"));

    private static ShaderModule CreateModule(VulkanContext context, byte[] code)
    {
        fixed (byte* ptr = code)
        {
            var createInfo = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)ptr,
            };
            context.Api.CreateShaderModule(context.Device, &createInfo, null, out var module).Check();
            return module;
        }
    }
}
