using System.Numerics;
using Rendition;

namespace Super;

/// <summary>A picture made by code, as RGBA bytes row by row. It must be square.</summary>
public sealed record GeneratedTexture(int Size, byte[] Pixels);

/// <summary>How a generated mesh looks. Everything is optional; the default is matte grey.</summary>
public sealed record Surface
{
    /// <summary>How many numbers a surface shader can take.</summary>
    public const int MaxParameters = Rendition.ShaderParameters.Length;

    /// <summary>How many textures, and how many texture arrays, a surface shader can take.</summary>
    public const int MaxShaderTextures = Rendition.ShaderTextures.Length;

    public Vector3 Color { get; init; } = new(0.7f);
    public float Roughness { get; init; } = 0.4f;
    public float Metallic { get; init; }
    public TextureRef? ColorTexture { get; init; }
    public TextureRef? NormalTexture { get; init; }
    public TextureRef? SurfaceTexture { get; init; }

    /// <summary>A fragment shader named through <see cref="Content.Shader"/>, or the standard one.</summary>
    public ShaderRef? Shader { get; init; }

    /// <summary>Numbers the shader reads as shaderParameters[i]. Up to <see cref="MaxParameters"/>.</summary>
    public Vector4[] Parameters { get; init; } = [];

    /// <summary>Textures the shader reads with shaderTexture(i, uv). Up to <see cref="MaxShaderTextures"/>.</summary>
    public TextureRef[] ShaderTextures { get; init; } = [];

    /// <summary>Texture arrays the shader reads with shaderLayer(i, uv, layer). Up to <see cref="MaxShaderTextures"/>.</summary>
    public TextureArrayRef[] ShaderArrays { get; init; } = [];
}

/// <summary>
/// A surface made by code rather than loaded from a file, such as generated ground. Simplify
/// is off for terrain chunks, whose edges must stay exactly where their neighbours expect.
/// </summary>
public sealed record GeneratedMesh(Vertex[] Vertices, uint[] Indices, Surface Surface, bool Simplify = true)
{
    /// <summary>The old shape: one colour texture and a roughness. The texture is owned by the mesh and freed with it.</summary>
    public GeneratedMesh(Vertex[] vertices, uint[] indices, GeneratedTexture texture, float roughness)
        : this(vertices, indices, new Surface { Color = Vector3.One, Roughness = roughness })
    {
        OwnedTexture = texture;
    }

    /// <summary>A colour texture that lives and dies with this mesh, from the old constructor.</summary>
    internal GeneratedTexture? OwnedTexture { get; init; }
}
