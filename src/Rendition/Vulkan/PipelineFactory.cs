using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace Rendition.Vulkan;

/// <summary>What differs between the renderer's graphics pipelines.</summary>
internal ref struct PipelineDescription
{
    public string VertexShader;
    public string FragmentShader;
    public PipelineLayout Layout;
    public ReadOnlySpan<Format> ColorFormats;
    public Format DepthFormat;
    public ReadOnlySpan<VertexInputBindingDescription> VertexBindings;
    public ReadOnlySpan<VertexInputAttributeDescription> VertexAttributes;
}

internal static unsafe class PipelineFactory
{
    public static Pipeline Create(VulkanContext context, in PipelineDescription description)
    {
        var api = context.Api;
        var vertexModule = LoadShader(context, description.VertexShader);
        var fragmentModule = LoadShader(context, description.FragmentShader);
        nint entryPoint = SilkMarshal.StringToPtr("main");

        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = vertexModule,
            PName = (byte*)entryPoint,
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = fragmentModule,
            PName = (byte*)entryPoint,
        };

        bool hasDepth = description.DepthFormat != Format.Undefined;
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
            CullMode = hasDepth ? CullModeFlags.BackBit : CullModeFlags.None,
            FrontFace = FrontFace.CounterClockwise,
            LineWidth = 1f,
        };
        var multisample = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };
        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable = hasDepth,
            DepthWriteEnable = hasDepth,
            DepthCompareOp = CompareOp.Greater, // Reversed-Z: nearer surfaces have larger depth.
        };

        int colorCount = description.ColorFormats.Length;
        var blendAttachments = stackalloc PipelineColorBlendAttachmentState[colorCount];
        for (int i = 0; i < colorCount; i++)
        {
            blendAttachments[i] = new PipelineColorBlendAttachmentState
            {
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
                ColorAttachmentCount = (uint)colorCount,
                PColorAttachmentFormats = colorFormats,
                DepthAttachmentFormat = description.DepthFormat,
            };
            var createInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                PNext = &rendering,
                StageCount = 2,
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
        api.DestroyShaderModule(context.Device, fragmentModule, null);
        return pipeline;
    }

    private static ShaderModule LoadShader(VulkanContext context, string name)
    {
        byte[] code = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "shaders", name + ".spv"));
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
