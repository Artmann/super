namespace Rendition;

public readonly record struct MeshHandle(int Index);

public readonly record struct TextureHandle(int Index);

/// <summary>A stack of same-sized images that shaders index by layer.</summary>
public readonly record struct TextureArrayHandle(int Index);

/// <summary>A photographed sky made by <see cref="Renderer.CreateSkyImage"/>.</summary>
public readonly record struct SkyImageHandle(int Index);

public readonly record struct MaterialHandle(int Index);

public readonly record struct SurfaceShaderHandle(int Index);

public readonly record struct ReflectionProbeHandle(int Index);

/// <summary>Bounds and simpler versions for a <see cref="DrawItem"/>. The default stands for none.</summary>
public readonly record struct DetailHandle(int Index);

/// <summary>What a texture reads past its edge, chosen once when it's created.</summary>
public enum TextureEdges
{
    /// <summary>The texture tiles, so the far edge shows. Most textures want this.</summary>
    Repeat,

    /// <summary>The edge texel carries on, for atlases whose far edge belongs to another island.</summary>
    Clamp,
}
