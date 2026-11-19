using Rendition.Vulkan;

namespace Rendition;

/// <summary>What a material must hold before it's made or changed: sane values, and a shader and textures that exist.</summary>
internal static class MaterialChecks
{
    /// <summary>
    /// Throws for a material with a value out of range, a surface shader that doesn't exist or that a transparent
    /// material asks for, or a shader texture that doesn't exist. <paramref name="shaderCount"/> is how many surface
    /// shaders there are, the built-in one at 0 included.
    /// </summary>
    public static void Check(in Material material, ResourceTable resources, int shaderCount)
    {
        if (material.Problem() is { } problem)
        {
            throw new ArgumentException($"The material's {problem}", nameof(material));
        }

        if (material.Custom.Shader is { } shader)
        {
            CheckShader(material, shader, shaderCount);
        }

        CheckShaderTextures(material, resources);
    }

    // Throws for a surface shader that doesn't exist, or on a transparent material, which can't use one yet.
    private static void CheckShader(in Material material, SurfaceShaderHandle shader, int shaderCount)
    {
        if (shader.Index <= 0 || shader.Index >= shaderCount)
        {
            throw new ArgumentException("The material's shader does not exist.", nameof(material));
        }

        if ((material.Flags & MaterialFlags.Transparent) != 0)
        {
            throw new ArgumentException("Transparent materials can't use a surface shader yet.", nameof(material));
        }
    }

    // Throws for a shader texture or texture array the material names that doesn't exist.
    private static void CheckShaderTextures(in Material material, ResourceTable resources)
    {
        for (int i = 0; i < ShaderTextures.Length; i++)
        {
            if (material.Custom.Textures[i] is { } texture && !resources.Textures.IsLive(texture.Index))
            {
                throw new ArgumentException($"The material's shader texture {i} does not exist.", nameof(material));
            }

            if (material.Custom.Arrays[i] is { } array && !resources.TextureArrays.IsLive(array.Index))
            {
                throw new ArgumentException($"The material's shader texture array {i} does not exist.", nameof(material));
            }
        }
    }
}
