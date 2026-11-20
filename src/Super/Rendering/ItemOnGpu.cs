using Rendition;

namespace Super;

/// <summary>What a content item became on the GPU, kept by <see cref="UploadedItems"/> at the index its ref carries.</summary>
internal abstract record ItemOnGpu;

/// <summary>
/// A model's parts. Material is set for a generated mesh: its own when it has an OwnedTexture,
/// otherwise shared through SharedMaterials. JointCount is 0 for a model with no skeleton.
/// </summary>
internal sealed record ModelOnGpu(DrawItem[] Parts, MaterialHandle? Material, TextureHandle? OwnedTexture, int JointCount = 0) : ItemOnGpu;

internal sealed record ShaderOnGpu(SurfaceShaderHandle Handle) : ItemOnGpu;

internal sealed record TextureOnGpu(TextureHandle Handle) : ItemOnGpu;

internal sealed record TextureArrayOnGpu(TextureArrayHandle Handle) : ItemOnGpu;

internal sealed record SkyOnGpu(SkyImageHandle Handle) : ItemOnGpu;

/// <summary>An artist's colour table, as the renderer numbers it rather than the content.</summary>
internal sealed record LutOnGpu(ColorLut Handle) : ItemOnGpu;

/// <summary>Stands where an item used to be, so using it again fails clearly.</summary>
internal sealed record ItemLetGo : ItemOnGpu
{
    public static ItemLetGo Instance { get; } = new();
}
