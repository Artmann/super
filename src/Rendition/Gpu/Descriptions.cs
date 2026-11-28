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
