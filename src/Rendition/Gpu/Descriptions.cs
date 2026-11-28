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

/// <summary>What a sampler reads past a texture's edges.</summary>
internal enum SamplerAddress
{
    /// <summary>The edge texel.</summary>
    Clamp,

    /// <summary>The texture again, so it tiles.</summary>
    Repeat,
}

/// <summary>
/// A sampler that clamps at the edges unless <see cref="Address"/> says otherwise. It reads only the first mip unless
/// <see cref="Mipmapped"/>, when it blends between mips the way <see cref="Filter"/> blends texels, as far down as the
/// view goes.
/// </summary>
internal readonly record struct SamplerDescription(SamplerFilter Filter, SamplerAddress Address = SamplerAddress.Clamp, bool Mipmapped = false);

/// <summary>How a vertex attribute is laid out in its buffer, named the way WebGPU names them.</summary>
internal enum VertexFormat
{
    Float32,
    Float32x2,
    Float32x3,
    Float32x4,
    Uint32,
    Uint8x4,

    /// <summary>Four bytes read as 0 to 1, the first in the lowest byte.</summary>
    Unorm8x4,
}

/// <summary>One input of the vertex shader: its location, and where in each vertex it is.</summary>
internal readonly record struct VertexAttribute(int Location, VertexFormat Format, int Offset);

/// <summary>
/// What one vertex buffer slot holds: <see cref="Stride"/> bytes for each vertex, or for each instance when
/// <see cref="PerInstance"/>. Layout i is for slot i.
/// </summary>
internal readonly record struct VertexLayout(int Stride, bool PerInstance, VertexAttribute[] Attributes);

/// <summary>Which faces a pipeline leaves out. A triangle faces the camera when its corners go counter-clockwise on the screen.</summary>
internal enum CullMode
{
    None,
    Back,
    Front,
}

/// <summary>What a pixel's depth must be to the depth already there to be drawn. Reversed-Z: nearer is larger.</summary>
internal enum CompareFunction
{
    Always,
    Greater,
    GreaterEqual,
    Equal,
}

/// <summary>A pipeline's depth target, how it tests against it, and whether it writes it.</summary>
internal readonly record struct DepthState(GpuFormat Format, CompareFunction Compare, bool Write);

/// <summary>What a colour or alpha is multiplied by before the two are added.</summary>
internal enum BlendFactor
{
    Zero,
    One,
    SrcAlpha,
    OneMinusSrcAlpha,
}

/// <summary>
/// How a colour target adds what is drawn to what it held: the drawn colour times <see cref="SourceColor"/> plus the
/// held colour times <see cref="TargetColor"/>, and the same for alpha.
/// </summary>
internal readonly record struct BlendState(BlendFactor SourceColor, BlendFactor TargetColor, BlendFactor SourceAlpha, BlendFactor TargetAlpha)
{
    /// <summary>Premultiplied colour over what the target holds.</summary>
    public static BlendState Premultiplied => new(BlendFactor.One, BlendFactor.OneMinusSrcAlpha, BlendFactor.One, BlendFactor.OneMinusSrcAlpha);
}

/// <summary>Which channels of a colour target a pipeline writes.</summary>
[Flags]
internal enum ColorWrites
{
    None = 0,
    Red = 1,
    Green = 2,
    Blue = 4,
    Alpha = 8,
    Color = Red | Green | Blue,
    All = Color | Alpha,
}

/// <summary>A colour target of a pipeline: its format, how it blends, if it does, and which channels it writes.</summary>
internal readonly record struct ColorTargetState(GpuFormat Format, BlendState? Blend = null, ColorWrites Writes = ColorWrites.All);

/// <summary>
/// A pipeline that draws triangles into <see cref="Targets"/>, reading <see cref="Vertices"/> from vertex buffers and
/// testing against <see cref="Depth"/> when it has one. Clip space is Vulkan's: y points down the screen, and depth
/// goes from 0 to 1.
/// </summary>
internal readonly ref struct RenderPipelineDescription
{
    public required ShaderId VertexShader { get; init; }

    public ShaderId FragmentShader { get; init; }

    public required ReadOnlySpan<ColorTargetState> Targets { get; init; }

    /// <summary>One layout per vertex buffer slot. Empty for a pipeline whose vertex shader makes its own corners.</summary>
    public ReadOnlySpan<VertexLayout> Vertices { get; init; }

    public DepthState? Depth { get; init; }

    public CullMode Cull { get; init; }

    /// <summary>
    /// The values of the shaders' override constants, where value i is for constant_id i. A constant without one
    /// keeps its default from the shader.
    /// </summary>
    public ReadOnlySpan<uint> Overrides { get; init; }
}

/// <summary>A pipeline that runs one compute shader.</summary>
internal readonly ref struct ComputePipelineDescription
{
    public required ShaderId Shader { get; init; }
}
