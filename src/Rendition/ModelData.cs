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
}

/// <summary>
/// A surface shader a model's material is drawn with, by name, as <c>Material.Shader</c> is for a
/// generated mesh. The game finds the shader and the textures by name when it loads the model.
/// </summary>
/// <param name="Name">The shader's file name without its extension, such as <c>puddles</c> for <c>Shaders/puddles.frag</c>.</param>
/// <param name="Parameters">What the shader reads as <c>shaderParameters[i]</c>. At most <see cref="ShaderParameters.Length"/>.</param>
/// <param name="Textures">The cooked names of the textures it reads with <c>shaderTexture(i, uv)</c>. At most <see cref="ShaderTextures.Length"/>.</param>
public sealed record ModelMaterialShader(string Name, Vector4[] Parameters, string[] Textures);

/// <summary>
/// Masks the cooker baked from a material's mesh, and the wear and dirt laid by them. See <see cref="Material.MaskTexture"/>.
/// </summary>
/// <param name="Texture">The mask texture's index in <see cref="ModelData.Textures"/>.</param>
/// <param name="Height">The metres its blue channel spans.</param>
/// <param name="Wear">Worn edges, laid where the mask is convex.</param>
/// <param name="Dirt">Dirt, laid in hollows and near the base.</param>
public sealed record ModelMaterialMasks(int Texture, float Height, MaterialLayer Wear, MaterialLayer Dirt);

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
    private const uint Magic = 0x4C444D52; // "RMDL"
    // 4: materials gained Subsurface. 5: height and detail textures. 6: materials gained Ior. 7: part names sit at the front.
    // 8: a texture is either inside the file or named by the hash of an object of its own. 9: textures say their format.
    // 10: material shaders. 11: vertices gained Uv2, and materials masks.
    private const int Version = 11;

    private const byte TextureInside = 0;
    private const byte TextureByHash = 1;

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
    }

    private (float Lowest, float Highest) Alpha(int texture) => texture >= 0 ? Textures[texture].AlphaRange() : (1f, 1f);

    public void Write(string path)
    {
        File.WriteAllBytes(path, ToBytes());
    }

    /// <summary>The bytes of a <c>.rmodel</c> file, as <see cref="Write"/> stores them, with every texture inside.</summary>
    public byte[] ToBytes() => ToBytes(null);

    /// <summary>
    /// The bytes of a <c>.rmodel</c> file whose textures are kept elsewhere. <paramref name="share"/> stores a
    /// texture and returns the name it's kept under, which <see cref="Read(Stream, string, Func{string, TextureData}?)"/>
    /// hands back to find it. Models that use the same texture then share one copy.
    /// </summary>
    public byte[] ToBytes(Func<TextureData, string>? share)
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

        if (MaterialShaders.Length != 0 && MaterialShaders.Length != Materials.Length)
        {
            throw new InvalidOperationException(
                $"The model has {Materials.Length} materials and {MaterialShaders.Length} material shaders. Give one to each material, or none.");
        }

        if (MaterialMasks.Length != 0 && MaterialMasks.Length != Materials.Length)
        {
            throw new InvalidOperationException(
                $"The model has {Materials.Length} materials and {MaterialMasks.Length} material masks. Give one to each material, or none.");
        }

        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteTo(writer, share);
        }

        return stream.ToArray();
    }

    private void WriteTo(BinaryWriter writer, Func<TextureData, string>? share)
    {
        writer.Write(Magic);
        writer.Write(Version);

        // The part names come first, then the rig, so a game can read either without the geometry
        // and textures behind them.
        writer.Write(MeshNames.Length);
        foreach (string meshName in MeshNames)
        {
            writer.Write(meshName);
        }

        writer.Write(Skeleton is not null);
        if (Skeleton is { } skeleton)
        {
            writer.Write(skeleton.Count);
            foreach (string name in skeleton.Names)
            {
                writer.Write(name);
            }

            WriteArray(writer, skeleton.Parents);
            WriteArray(writer, skeleton.Rest);
            WriteArray(writer, skeleton.InverseBind);
            WriteArray(writer, new[] { skeleton.RootTransform });
        }

        writer.Write(Clips.Length);
        foreach (var clip in Clips)
        {
            writer.Write(clip.Name);
            writer.Write(clip.FrameRate);
            writer.Write(clip.JointCount);
            WriteArray(writer, clip.Frames);
        }

        WriteArray(writer, Vertices);
        WriteArray(writer, Indices);
        WriteArray(writer, Parts);
        WriteArray(writer, Materials);
        writer.Write(MaterialShaders.Length);
        foreach (var shader in MaterialShaders)
        {
            writer.Write(shader is not null);
            if (shader is not null)
            {
                writer.Write(shader.Name);
                WriteArray(writer, shader.Parameters);
                writer.Write(shader.Textures.Length);
                foreach (string texture in shader.Textures)
                {
                    writer.Write(texture);
                }
            }
        }

        writer.Write(MaterialMasks.Length);
        foreach (var masks in MaterialMasks)
        {
            writer.Write(masks is not null);
            if (masks is not null)
            {
                writer.Write(masks.Texture);
                writer.Write(masks.Height);
                WriteArray(writer, new[] { masks.Wear, masks.Dirt });
            }
        }

        writer.Write(Textures.Length);
        foreach (var texture in Textures)
        {
            if (share is null)
            {
                writer.Write(TextureInside);
                texture.WriteBody(writer);
            }
            else
            {
                writer.Write(TextureByHash);
                writer.Write(share(texture));
            }
        }
    }

    public static ModelData Read(string path)
    {
        using var stream = new BufferedStream(File.OpenRead(path), 1 << 20);
        return Read(stream, path);
    }

    /// <summary>Reads a model from <paramref name="stream"/>, which is left open. <paramref name="name"/> is only for messages.</summary>
    /// <param name="texture">
    /// Finds a texture the model keeps elsewhere, by the name <see cref="ToBytes(Func{TextureData, string}?)"/> gave it.
    /// Null when the model must have every texture inside.
    /// </param>
    public static ModelData Read(Stream stream, string name, Func<string, TextureData>? texture = null)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        ReadHeader(reader, name);
        string[] meshNames = ReadNames(reader);
        var (skeleton, clips) = ReadRigBody(reader);
        var vertices = ReadArray<Vertex>(reader);
        var indices = ReadArray<uint>(reader);
        var parts = ReadArray<ModelPart>(reader);
        var materials = ReadArray<ModelMaterial>(reader);
        var shaders = new ModelMaterialShader?[reader.ReadInt32()];
        for (int i = 0; i < shaders.Length; i++)
        {
            if (reader.ReadBoolean())
            {
                string shaderName = reader.ReadString();
                var parameters = ReadArray<Vector4>(reader);
                shaders[i] = new ModelMaterialShader(shaderName, parameters, ReadNames(reader));
            }
        }

        var masks = new ModelMaterialMasks?[reader.ReadInt32()];
        for (int i = 0; i < masks.Length; i++)
        {
            if (reader.ReadBoolean())
            {
                int maskTexture = reader.ReadInt32();
                float height = reader.ReadSingle();
                var layers = ReadArray<MaterialLayer>(reader);
                masks[i] = new ModelMaterialMasks(maskTexture, height, layers[0], layers[1]);
            }
        }

        var textures = new TextureData[reader.ReadInt32()];
        for (int i = 0; i < textures.Length; i++)
        {
            byte form = reader.ReadByte();
            if (form == TextureInside)
            {
                textures[i] = TextureData.ReadBody(reader);
            }
            else if (form == TextureByHash)
            {
                string reference = reader.ReadString();
                textures[i] = texture?.Invoke(reference) ?? throw new InvalidDataException(
                    $"{name} keeps its textures in the cooked content beside it, so read it through Content, or cook it with super-cook file.");
            }
            else
            {
                throw new InvalidDataException($"{name} is damaged: texture {i} has the unknown form {form}. Cook it again.");
            }
        }

        return new ModelData
        {
            Vertices = vertices,
            Indices = indices,
            Parts = parts,
            Materials = materials,
            MaterialShaders = shaders,
            MaterialMasks = masks,
            Textures = textures,
            Skeleton = skeleton,
            Clips = clips,
            MeshNames = meshNames,
        };
    }

    /// <summary>Reads only the skeleton and clips, which sit at the front of the file, so it's quick.</summary>
    public static (Skeleton? Skeleton, AnimationClip[] Clips) ReadRig(string path)
    {
        using var stream = new BufferedStream(File.OpenRead(path), 1 << 16);
        return ReadRig(stream, path);
    }

    /// <summary>Reads only the skeleton and clips from <paramref name="stream"/>, which is left open. <paramref name="name"/> is only for messages.</summary>
    public static (Skeleton? Skeleton, AnimationClip[] Clips) ReadRig(Stream stream, string name) =>
        ReadRig(stream, name, out _);

    /// <summary>
    /// Reads only the skeleton and clips, and the mesh names that sit before them, from
    /// <paramref name="stream"/>, which is left open. <paramref name="name"/> is only for messages.
    /// </summary>
    public static (Skeleton? Skeleton, AnimationClip[] Clips) ReadRig(Stream stream, string name, out string[] meshNames)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        ReadHeader(reader, name);
        meshNames = ReadNames(reader);
        return ReadRigBody(reader);
    }

    /// <summary>
    /// Reads only the mesh names, which sit at the very front of the file, so a game can pick
    /// meshes without reading the rig or the geometry. <paramref name="name"/> is only for messages.
    /// </summary>
    public static string[] ReadMeshNames(Stream stream, string name)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        ReadHeader(reader, name);
        return ReadNames(reader);
    }

    private static void ReadHeader(BinaryReader reader, string name)
    {
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
        {
            throw new InvalidDataException($"{name} is not a current model file. Cook it again.");
        }
    }

    private static string[] ReadNames(BinaryReader reader)
    {
        var names = new string[reader.ReadInt32()];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = reader.ReadString();
        }

        return names;
    }

    private static (Skeleton? Skeleton, AnimationClip[] Clips) ReadRigBody(BinaryReader reader)
    {
        Skeleton? skeleton = null;
        if (reader.ReadBoolean())
        {
            var names = new string[reader.ReadInt32()];
            for (int i = 0; i < names.Length; i++)
            {
                names[i] = reader.ReadString();
            }

            var parents = ReadArray<int>(reader);
            var rest = ReadArray<JointPose>(reader);
            var inverseBind = ReadArray<Matrix4x4>(reader);
            var root = ReadArray<Matrix4x4>(reader)[0];
            skeleton = new Skeleton(names, parents, rest, inverseBind, root);
        }

        var clips = new AnimationClip[reader.ReadInt32()];
        for (int i = 0; i < clips.Length; i++)
        {
            string clipName = reader.ReadString();
            float frameRate = reader.ReadSingle();
            int jointCount = reader.ReadInt32();
            clips[i] = new AnimationClip(clipName, frameRate, jointCount, ReadArray<JointPose>(reader));
        }

        return (skeleton, clips);
    }

    private static void WriteArray<T>(BinaryWriter writer, T[] items) where T : unmanaged
    {
        writer.Write(items.Length);
        writer.Write(MemoryMarshal.AsBytes(items.AsSpan()));
    }

    private static T[] ReadArray<T>(BinaryReader reader) where T : unmanaged
    {
        var items = new T[reader.ReadInt32()];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(items.AsSpan()));
        return items;
    }
}
