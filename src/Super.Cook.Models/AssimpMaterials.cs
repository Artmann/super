using Silk.NET.Assimp;
using Assimp = Silk.NET.Assimp.Assimp;
using AssimpMaterial = Silk.NET.Assimp.Material;

namespace Super.Cook.Models;

/// <summary>What the model file says of a material: the textures it links and, for glTF only, its values.</summary>
internal sealed record FileMaterial(
    string? Color, string? Normal, string? Surface, float[]? BaseColor, float? Metallic, float? Roughness, float? Cutoff);

/// <summary>Reads materials as Assimp holds them.</summary>
internal static unsafe class AssimpMaterials
{
    public static string NameOf(Assimp assimp, AssimpMaterial* source)
    {
        AssimpString name;
        assimp.GetMaterialString(source, Assimp.MaterialNameBase, 0, 0, &name);
        return name.AsString;
    }

    public static FileMaterial Read(Assimp assimp, AssimpMaterial* source, bool isGltf)
    {
        string? color = GetTexture(assimp, source, TextureType.Diffuse) ?? GetTexture(assimp, source, TextureType.BaseColor);
        string? normal = GetTexture(assimp, source, TextureType.Normals) ?? GetTexture(assimp, source, TextureType.Height);
        if (!isGltf)
        {
            return new FileMaterial(color, normal, null, null, null, null, null);
        }

        // Only glTF defines these values and the combined metal and roughness texture.
        return new FileMaterial(
            color,
            normal,
            GetTexture(assimp, source, TextureType.Unknown) ?? GetTexture(assimp, source, TextureType.Metalness),
            GetFloats(assimp, source, "$clr.base", 4),
            GetFloats(assimp, source, "$mat.metallicFactor", 1)?[0],
            GetFloats(assimp, source, "$mat.roughnessFactor", 1)?[0],
            GltfCutoff(assimp, source));
    }

    /// <summary>The texture's file name, or null for none, and in <paramref name="uv"/> the UV set it's read through.</summary>
    public static string? GetTexture(Assimp assimp, AssimpMaterial* material, TextureType type, out uint uv)
    {
        AssimpString path;
        uint set = 0;
        var result = assimp.GetMaterialTexture(material, type, 0, &path, null, &set, null, null, null, null);
        uv = set;
        return result == Return.Success ? Path.GetFileName(path.AsString.Replace('\\', '/')) : null;
    }

    private static string? GetTexture(Assimp assimp, AssimpMaterial* material, TextureType type) =>
        GetTexture(assimp, material, type, out _);

    // glTF's MASK mode cuts out by alpha, at 0.5 unless the file says otherwise.
    private static float? GltfCutoff(Assimp assimp, AssimpMaterial* material)
    {
        AssimpString mode;
        if (assimp.GetMaterialString(material, "$mat.gltf.alphaMode", 0, 0, &mode) != Return.Success
            || mode.AsString != "MASK")
        {
            return null;
        }

        return GetFloats(assimp, material, "$mat.gltf.alphaCutoff", 1)?[0] ?? 0.5f;
    }

    private static float[]? GetFloats(Assimp assimp, AssimpMaterial* material, string key, uint count)
    {
        var values = new float[count];
        uint read = count;
        fixed (float* pointer = values)
        {
            return assimp.GetMaterialFloatArray(material, key, 0, 0, pointer, &read) == Return.Success && read == count
                ? values
                : null;
        }
    }
}
