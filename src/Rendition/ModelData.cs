using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Rendition;

public enum TextureKind
{
    /// <summary>Colour data stored as sRGB.</summary>
    Color,

    /// <summary>Non-colour data such as normals or surface masks.</summary>
    Linear,
}

/// <summary>A material as stored in a model file. Texture fields index <see cref="ModelData.Textures"/>, -1 for none.</summary>
public struct ModelMaterial
{
    public Vector4 BaseColor;
    public float Metallic;
    public float Roughness;
    public float AlphaCutoff;
    public int BaseColorTexture;
    public int NormalTexture;
    public int SurfaceTexture;
    public Vector3 Emissive;
    public int EmissiveTexture;
    public float Clearcoat;
    public int HeightTexture;
    public float HeightDepth;
    public int DetailColorTexture;
    public int DetailNormalTexture;
    public float DetailScale;
    public MaterialFlags Flags;
    public float Ior;
    public Subsurface Subsurface;

    /// <summary>For foliage, how much sunlight shines through from behind, 0 to 1. See <see cref="Material.Translucency"/>.</summary>
    public float Translucency;

    /// <summary>How far it sways in the wind, as a share of the view's strength; 0 keeps it still. See <see cref="Material.Wind"/>.</summary>
    public float Wind;
}

/// <summary>
/// A surface shader a model's material is drawn with, by name, as <c>Material.Custom.Shader</c> is for a
/// generated mesh. The game finds the shader and the textures by name when it loads the model.
/// </summary>
/// <param name="Name">The shader's file name without its extension, such as <c>puddles</c> for <c>Shaders/puddles.frag</c>.</param>
/// <param name="Parameters">What the shader reads as <c>shaderParameters[i]</c>. At most <see cref="ShaderParameters.Length"/>.</param>
/// <param name="Textures">The cooked names of the textures it reads with <c>shaderTexture(i, uv)</c>. At most <see cref="ShaderTextures.Length"/>.</param>
public sealed record ModelMaterialShader(string Name, Vector4[] Parameters, string[] Textures);

/// <summary>
/// Masks the cooker baked from a material's mesh, and the wear and dirt laid by them. See <see cref="MaterialWeathering.Mask"/>.
/// </summary>
/// <param name="Texture">The mask texture's index in <see cref="ModelData.Textures"/>.</param>
/// <param name="Wear">Worn edges, laid where the mask is convex.</param>
/// <param name="Dirt">Dirt, laid in hollows and near each mesh's foot.</param>
public sealed record ModelMaterialMasks(int Texture, MaterialLayer Wear, MaterialLayer Dirt);

/// <summary>A cooked material's cel shading. See <see cref="Material.Toon"/>.</summary>
/// <param name="Steps">How many bands the light falls into, from 1 to <see cref="ToonShading.MostSteps"/>.</param>
/// <param name="Ramp">The ramp texture's index in <see cref="ModelData.Textures"/>, or -1 for plain bands.</param>
/// <param name="Rim">Light added along the shape's outline.</param>
public readonly record struct ModelMaterialToon(int Steps, int Ramp, Vector3 Rim);

/// <summary>One run of triangles sharing a material.</summary>
public struct ModelPart
{
    public uint FirstIndex;
    public uint IndexCount;
    public int Material;
}

/// <summary>The cooked form of a model, produced by the model cooker in Super.Cook.Models.</summary>
public sealed class ModelData
{
    public required Vertex[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
    public required ModelPart[] Parts { get; init; }
    public required ModelMaterial[] Materials { get; init; }
    public required TextureData[] Textures { get; init; }

    /// <summary>
    /// For each part, the name of the source mesh it was cooked from: the node that holds the mesh.
    /// In the same order as <see cref="Parts"/>. Names repeat when a mesh with several materials is
    /// cooked as several parts. Empty when the model has none; otherwise it must be as long as <see cref="Parts"/>.
    /// </summary>
    public string[] MeshNames { get; init; } = [];

    /// <summary>The joints the vertices follow, for a rigged model. Null for a static one.</summary>
    public Skeleton? Skeleton { get; init; }

    /// <summary>Motions for the skeleton, each with as many joints as it has.</summary>
    public AnimationClip[] Clips { get; init; } = [];

    /// <summary>
    /// For each material, the surface shader it's drawn with, or null for the standard one. In the
    /// same order as <see cref="Materials"/>. Empty when no material has one; otherwise it must be as
    /// long as <see cref="Materials"/>. Read it with <see cref="ShaderOf"/>.
    /// </summary>
    public ModelMaterialShader?[] MaterialShaders { get; init; } = [];

    /// <summary>The surface shader material <paramref name="material"/> is drawn with, or null for the standard one.</summary>
    public ModelMaterialShader? ShaderOf(int material) =>
        material < MaterialShaders.Length ? MaterialShaders[material] : null;

    /// <summary>
    /// For each material, its baked masks with the wear and dirt laid by them, or null for none. In the same order as
    /// <see cref="Materials"/>. Empty when no material has them; otherwise it must be as long as
    /// <see cref="Materials"/>. Read it with <see cref="MaskOf"/>.
    /// </summary>
    public ModelMaterialMasks?[] MaterialMasks { get; init; } = [];

    /// <summary>Material <paramref name="material"/>'s baked masks, or null when it has none.</summary>
    public ModelMaterialMasks? MaskOf(int material) =>
        material < MaterialMasks.Length ? MaterialMasks[material] : null;

    /// <summary>
    /// For each material, its occlusion texture's index in <see cref="Textures"/>, or -1 for none: see
    /// <see cref="MaterialTextures.Occlusion"/>. The second UV set reads it where the material has
    /// <see cref="MaterialFlags.OcclusionOnUv2"/>. In the same order as <see cref="Materials"/>. Empty when no material
    /// has one; otherwise it must be as long as <see cref="Materials"/>. Read it with <see cref="OcclusionOf"/>.
    /// </summary>
    public int[] MaterialOcclusion { get; init; } = [];

    /// <summary>Material <paramref name="material"/>'s occlusion texture's index, or -1 when it has none.</summary>
    public int OcclusionOf(int material) =>
        material < MaterialOcclusion.Length ? MaterialOcclusion[material] : -1;

    /// <summary>
    /// For each material, its cel shading, or null for the physically based look: see <see cref="Material.Toon"/>. In
    /// the same order as <see cref="Materials"/>. Empty when no material is toon; otherwise it must be as long as
    /// <see cref="Materials"/>. Read it with <see cref="ToonOf"/>.
    /// </summary>
    public ModelMaterialToon?[] MaterialToon { get; init; } = [];

    /// <summary>Material <paramref name="material"/>'s cel shading, or null when it has none.</summary>
    public ModelMaterialToon? ToonOf(int material) =>
        material < MaterialToon.Length ? MaterialToon[material] : null;

    /// <summary>
    /// What texture <paramref name="texture"/> reads past its edge: <see cref="TextureEdges.Clamp"/> when it's
    /// a material's masks or an occlusion texture on the second UV set, which are atlases of the mesh's islands, and
    /// <see cref="TextureEdges.Repeat"/> otherwise.
    /// </summary>
    public TextureEdges EdgesOf(int texture)
    {
        return MaterialMasks.Any(masks => masks?.Texture == texture) || IsOcclusionOnUv2()
            ? TextureEdges.Clamp
            : TextureEdges.Repeat;

        bool IsOcclusionOnUv2()
        {
            for (int i = 0; i < MaterialOcclusion.Length; i++)
            {
                if (MaterialOcclusion[i] == texture && (Materials[i].Flags & MaterialFlags.OcclusionOnUv2) != 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Says why a material's cutoff can't work, or returns null when it can or there is
    /// none. Opacity is the colour texture's alpha times BaseColor.W times the surface
    /// texture's alpha, so a cutoff with no alpha below it keeps every pixel, and one
    /// above all of them removes the whole material.
    /// </summary>
    public string? CutoutProblem(int material)
    {
        ref readonly var source = ref Materials[material];
        if (source.AlphaCutoff <= 0f)
        {
            return null;
        }

        var color = Alpha(source.BaseColorTexture);
        var surface = Alpha(source.SurfaceTexture);
        float lowest = color.Lowest * source.BaseColor.W * surface.Lowest;
        float highest = color.Highest * source.BaseColor.W * surface.Highest;
        string cutoff = source.AlphaCutoff.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (lowest >= source.AlphaCutoff)
        {
            return source.BaseColorTexture < 0 && source.SurfaceTexture < 0
                ? $"alpha cutoff {cutoff} has nothing to cut by, because the material has no colour or surface texture."
                : $"alpha cutoff {cutoff} cuts nothing out, because no pixel of its colour or surface texture has alpha below it.";
        }

        if (highest < source.AlphaCutoff)
        {
            return $"alpha cutoff {cutoff} cuts out every pixel, because none has alpha that high.";
        }

        return null;

        (float Lowest, float Highest) Alpha(int texture) => texture >= 0 ? Textures[texture].AlphaRange() : (1f, 1f);
    }

    public void Write(string path)
    {
        File.WriteAllBytes(path, ToBytes());
    }

    /// <summary>
    /// The bytes of a <c>.rmodel</c> file, as <see cref="Write"/> stores them, with every texture inside, or with the
    /// textures kept elsewhere when <paramref name="share"/> is given. It stores a texture and returns the name it's kept
    /// under, which <see cref="ModelFile.Read(Stream, string, Func{string, TextureData}?)"/> hands back to find it. Models
    /// that use the same texture then share one copy.
    /// </summary>
    public byte[] ToBytes(Func<TextureData, string>? share = null)
    {
        foreach (var clip in Clips)
        {
            if (Skeleton is null || clip.JointCount != Skeleton.Count)
            {
                throw new InvalidOperationException(
                    $"Clip {clip.Name} has {clip.JointCount} joints, but the model has {Skeleton?.Count ?? 0}.");
            }
        }

        if (MeshNames.Length != 0 && MeshNames.Length != Parts.Length)
        {
            throw new InvalidOperationException(
                $"The model has {Parts.Length} parts and {MeshNames.Length} mesh names. Give one name to each part, or none.");
        }

        RequireOnePerMaterial(MaterialShaders.Length, "material shaders", "or none");
        RequireOnePerMaterial(MaterialMasks.Length, "material masks", "or none");
        RequireOnePerMaterial(MaterialOcclusion.Length, "occlusion textures", "-1 for none, or none at all");
        RequireOnePerMaterial(MaterialToon.Length, "toon settings", "null for none, or none at all");

        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            ModelFile.WriteTo(writer, this, share);
        }

        return stream.ToArray();

        // A list that goes with the materials must have one entry for each of them, or be empty.
        void RequireOnePerMaterial(int count, string what, string otherwise)
        {
            if (count != 0 && count != Materials.Length)
            {
                throw new InvalidOperationException(
                    $"The model has {Materials.Length} materials and {count} {what}. Give one to each material, {otherwise}.");
            }
        }
    }
}
