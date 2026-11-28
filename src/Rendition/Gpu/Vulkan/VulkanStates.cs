using Silk.NET.Vulkan;

namespace Rendition.Gpu.Vulkan;

/// <summary>The Vulkan pipeline state for each of the layer's.</summary>
internal static class VulkanStates
{
    /// <summary>How many attributes <paramref name="layouts"/> hold together.</summary>
    public static int AttributeCount(ReadOnlySpan<VertexLayout> layouts)
    {
        int count = 0;
        foreach (var layout in layouts)
        {
            count += layout.Attributes.Length;
        }

        return count;
    }

    /// <summary>One binding per layout, binding i for slot i, and every layout's attributes after one another.</summary>
    public static void Fill(
        ReadOnlySpan<VertexLayout> layouts, Span<VertexInputBindingDescription> bindings, Span<VertexInputAttributeDescription> attributes)
    {
        int next = 0;
        for (int slot = 0; slot < layouts.Length; slot++)
        {
            var layout = layouts[slot];
            bindings[slot] = new VertexInputBindingDescription(
                (uint)slot, (uint)layout.Stride, layout.PerInstance ? VertexInputRate.Instance : VertexInputRate.Vertex);
            foreach (var attribute in layout.Attributes)
            {
                attributes[next++] = new VertexInputAttributeDescription(
                    (uint)attribute.Location, (uint)slot, Of(attribute.Format), (uint)attribute.Offset);
            }
        }
    }

    /// <summary>Each target's blend, or none, and the channels it writes.</summary>
    public static void Fill(ReadOnlySpan<ColorTargetState> targets, Span<Format> formats, Span<PipelineColorBlendAttachmentState> blends)
    {
        for (int i = 0; i < targets.Length; i++)
        {
            var target = targets[i];
            formats[i] = VulkanFormats.Of(target.Format);
            var blend = target.Blend ?? default;
            blends[i] = new PipelineColorBlendAttachmentState
            {
                BlendEnable = target.Blend is not null,
                SrcColorBlendFactor = Of(blend.SourceColor),
                DstColorBlendFactor = Of(blend.TargetColor),
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = Of(blend.SourceAlpha),
                DstAlphaBlendFactor = Of(blend.TargetAlpha),
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = (ColorComponentFlags)(int)target.Writes,
            };
        }
    }

    public static CullModeFlags Of(CullMode cull) => cull switch
    {
        CullMode.Back => CullModeFlags.BackBit,
        CullMode.Front => CullModeFlags.FrontBit,
        _ => CullModeFlags.None,
    };

    public static CompareOp Of(CompareFunction compare) => compare switch
    {
        CompareFunction.Greater => CompareOp.Greater,
        CompareFunction.GreaterEqual => CompareOp.GreaterOrEqual,
        CompareFunction.Equal => CompareOp.Equal,
        _ => CompareOp.Always,
    };

    private static Silk.NET.Vulkan.BlendFactor Of(BlendFactor factor) => factor switch
    {
        BlendFactor.One => Silk.NET.Vulkan.BlendFactor.One,
        BlendFactor.SrcAlpha => Silk.NET.Vulkan.BlendFactor.SrcAlpha,
        BlendFactor.OneMinusSrcAlpha => Silk.NET.Vulkan.BlendFactor.OneMinusSrcAlpha,
        _ => Silk.NET.Vulkan.BlendFactor.Zero,
    };

    private static Format Of(VertexFormat format) => format switch
    {
        VertexFormat.Float32 => Format.R32Sfloat,
        VertexFormat.Float32x2 => Format.R32G32Sfloat,
        VertexFormat.Float32x3 => Format.R32G32B32Sfloat,
        VertexFormat.Float32x4 => Format.R32G32B32A32Sfloat,
        VertexFormat.Uint32 => Format.R32Uint,
        VertexFormat.Sint32x3 => Format.R32G32B32Sint,
        VertexFormat.Uint8x4 => Format.R8G8B8A8Uint,
        VertexFormat.Unorm8x4 => Format.R8G8B8A8Unorm,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };
}
