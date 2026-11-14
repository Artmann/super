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
    /// overestimation. Only where <see cref="VulkanContext.ConservativeRasterization"/> is true.
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
/// So keep this object, never a copy of <see cref="Handle"/>, and end it with <see cref="Destroy"/>.
/// </summary>
internal sealed class LivePipeline(VulkanContext context, Pipeline handle, Func<Pipeline>? rebuild, string[] shaders)
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
        context.LivePipelines?.Remove(this);
        context.Api.DestroyPipeline(context.Device, Handle, null);
        Handle = default;
    }
}

internal static unsafe class PipelineFactory
{
    public static LivePipeline Create(VulkanContext context, in PipelineDescription description)
    {
        var handle = Build(context, description);
        if (context.LivePipelines is not { } live)
        {
            return new LivePipeline(context, handle, null, []);
        }

        var kept = Keep(description);
        string? fragment = description.FragmentCode is { } code ? code.Name : description.FragmentShader;
        string[] shaders = fragment is null ? [description.VertexShader] : [description.VertexShader, fragment];
        var pipeline = new LivePipeline(context, handle, () => Build(context, kept.Restore()), shaders);
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
                context.Api.DeviceWaitIdle(context.Device).Check();
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
        ShaderModule fragmentModule = default;
        if (description.FragmentCode is { } code)
        {
            fragmentModule = CreateModule(context, code.Bytes);
        }
        else if (description.FragmentShader is { } fragmentShader)
        {
            fragmentModule = LoadShader(context, fragmentShader);
        }

        bool hasFragment = fragmentModule.Handle != 0;
        nint entryPoint = SilkMarshal.StringToPtr("main");

        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertexModule,
            PName = (byte*)entryPoint,
        };
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
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragmentModule,
            PName = (byte*)entryPoint,
            PSpecializationInfo = &specialization,
        };

        bool hasDepth = description.DepthFormat != Format.Undefined && !description.IgnoreDepth;
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
        };
        var viewport = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1,
        };
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = description.CullMode
                ?? (hasDepth && !description.TwoSided ? CullModeFlags.BackBit : CullModeFlags.None),
            FrontFace = FrontFace.CounterClockwise,
            LineWidth = 1f,
        };
        var conservative = new PipelineRasterizationConservativeStateCreateInfoEXT
        {
            SType = StructureType.PipelineRasterizationConservativeStateCreateInfoExt,
            ConservativeRasterizationMode = ConservativeRasterizationModeEXT.OverestimateExt,
        };
        if (description.Conservative)
        {
            if (!context.ConservativeRasterization)
            {
                throw new InvalidOperationException("Conservative rasterization needs VK_EXT_conservative_rasterization, which this device lacks.");
            }

            rasterizer.PNext = &conservative;
        }

        var multisample = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = description.Samples == 0 ? SampleCountFlags.Count1Bit : description.Samples,
        };
        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = hasDepth,
            DepthWriteEnable = hasDepth && description.WritesDepth,
            DepthCompareOp = description.DepthCompare,
        };

        int colorCount = description.ColorFormats.Length;
        var blendAttachments = stackalloc PipelineColorBlendAttachmentState[colorCount];
        for (int i = 0; i < colorCount; i++)
        {
            if (!description.Blends.IsEmpty)
            {
                blendAttachments[i] = description.Blends[i];
                continue;
            }

            blendAttachments[i] = new PipelineColorBlendAttachmentState
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

        var blend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = (uint)colorCount,
            PAttachments = blendAttachments,
        };
        var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates = dynamicStates,
        };

        Pipeline pipeline;
        fixed (Format* colorFormats = description.ColorFormats)
        fixed (VertexInputBindingDescription* bindings = description.VertexBindings)
        fixed (VertexInputAttributeDescription* attributes = description.VertexAttributes)
        {
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = (uint)description.VertexBindings.Length,
                PVertexBindingDescriptions = bindings,
                VertexAttributeDescriptionCount = (uint)description.VertexAttributes.Length,
                PVertexAttributeDescriptions = attributes,
            };
            var rendering = new PipelineRenderingCreateInfo
            {
                SType = StructureType.PipelineRenderingCreateInfo,
                ViewMask = description.ViewMask,
                ColorAttachmentCount = (uint)colorCount,
                PColorAttachmentFormats = colorFormats,
                DepthAttachmentFormat = description.DepthFormat,
            };
            var createInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = hasFragment ? 2u : 1u,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewport,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisample,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &blend,
                PDynamicState = &dynamicState,
                Layout = description.Layout,
            };
            api.CreateGraphicsPipelines(context.Device, default, 1, &createInfo, null, &pipeline).Check();
        }

        SilkMarshal.Free(entryPoint);
        api.DestroyShaderModule(context.Device, vertexModule, null);
        if (hasFragment)
        {
            api.DestroyShaderModule(context.Device, fragmentModule, null);
        }

        return pipeline;
    }

    public static LivePipeline CreateCompute(VulkanContext context, string shader, PipelineLayout layout)
    {
        var handle = BuildCompute(context, shader, layout);
        if (context.LivePipelines is not { } live)
        {
            return new LivePipeline(context, handle, null, []);
        }

        var pipeline = new LivePipeline(context, handle, () => BuildCompute(context, shader, layout), [shader]);
        live.Add(pipeline);
        return pipeline;
    }

    private static Pipeline BuildCompute(VulkanContext context, string shader, PipelineLayout layout)
    {
        var api = context.Api;
        var module = LoadShader(context, shader);
        nint entryPoint = SilkMarshal.StringToPtr("main");
        var createInfo = new ComputePipelineCreateInfo
        {
            SType = StructureType.ComputePipelineCreateInfo,
            Stage = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.ComputeBit,
                Module = module,
                PName = (byte*)entryPoint,
            },
            Layout = layout,
        };

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
