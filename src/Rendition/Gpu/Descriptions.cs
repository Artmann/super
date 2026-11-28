using System.Numerics;

namespace Rendition.Gpu;

/// <summary>What a render pass does with what a target held before it.</summary>
internal enum LoadOp
{
    Load,
    Clear,
    DontCare,
}

/// <summary>A colour target of a render pass, and the colour it is cleared to when <see cref="Load"/> is <see cref="LoadOp.Clear"/>.</summary>
internal readonly record struct ColorTarget(ViewId View, LoadOp Load, Vector4 Clear);

/// <summary>The depth target of a render pass. A read-only one can be bound for sampling in the same pass.</summary>
internal readonly record struct DepthTarget(ViewId View, LoadOp Load, float Clear, bool ReadOnly);

/// <summary>How a sampler blends the texels around where it reads.</summary>
internal enum SamplerFilter
{
    Nearest,
    Linear,
}

/// <summary>A sampler that clamps at the edges and reads the first mip.</summary>
internal readonly record struct SamplerDescription(SamplerFilter Filter);

/// <summary>
/// A pipeline that draws triangles into colour targets of <see cref="ColorFormats"/>, with no depth. With
/// <see cref="Blend"/> it adds premultiplied colour over what the targets hold instead of replacing it.
/// </summary>
internal readonly ref struct RenderPipelineDescription
{
    public required ShaderId VertexShader { get; init; }

    public ShaderId FragmentShader { get; init; }

    public required ReadOnlySpan<GpuFormat> ColorFormats { get; init; }

    public bool Blend { get; init; }
}

/// <summary>A pipeline that runs one compute shader.</summary>
internal readonly ref struct ComputePipelineDescription
{
    public required ShaderId Shader { get; init; }
}
