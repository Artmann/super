using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Rendition;

/// <summary>Reads and writes <c>.rmodel</c> files, the cooked form of a <see cref="ModelData"/>.</summary>
public static class ModelFile
{
    private const uint Magic = 0x4C444D52; // "RMDL"
    // 4: materials gained Subsurface. 5: height and detail textures. 6: materials gained Ior. 7: part names sit at the front.
    // 8: a texture is either inside the file or named by the hash of an object of its own. 9: textures say their format.
    // 10: material shaders. 11: vertices gained Uv2, and materials masks. 12: masks lost their height.
    // 13: materials gained Translucency and Wind. 14: skeletons gained the bounds of the vertices each joint moves.
    // 15: clips carry their events.
    private const int Version = 16;

    // Marks the optional sections after the textures, which a model without them leaves out, so its bytes, and the bake
    // ids made from them, stay as they were before the section existed. The end of the stream ends the file.
    private const byte OcclusionSection = 1;
    private const byte ToonSection = 2;

    public static ModelData Read(string path)
    {
        using var stream = new BufferedStream(File.OpenRead(path), 1 << 20);
        return Read(stream, path);
    }

    /// <summary>Reads a model from <paramref name="stream"/>, which is left open. <paramref name="name"/> is only for messages.</summary>
    /// <param name="texture">
    /// Finds a texture the model keeps elsewhere, by the name <see cref="ModelData.ToBytes(Func{TextureData, string}?)"/>
    /// gave it. Null when the model must have every texture inside.
    /// </param>
    public static ModelData Read(Stream stream, string name, Func<string, TextureData>? texture = null)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        ReadHeader(reader, name);
        string[] meshNames = ModelFileParts.ReadNames(reader);
        var (skeleton, clips) = ModelFileParts.ReadRig(reader);
        var vertices = ModelFileParts.ReadArray<Vertex>(reader);
        var indices = ModelFileParts.ReadArray<uint>(reader);
        var parts = ModelFileParts.ReadArray<ModelPart>(reader);
        var materials = ModelFileParts.ReadArray<ModelMaterial>(reader);
        var shaders = ModelFileParts.ReadShaders(reader);
        var masks = ModelFileParts.ReadMasks(reader);
        var textures = ModelFileParts.ReadTextures(reader, name, texture);
        var (occlusion, toon) = ReadSections(stream, reader, name);
        return new ModelData
        {
            Vertices = vertices,
            Indices = indices,
            Parts = parts,
            Materials = materials,
            MaterialShaders = shaders,
            MaterialMasks = masks,
            MaterialOcclusion = occlusion,
            MaterialToon = toon,
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
        meshNames = ModelFileParts.ReadNames(reader);
        return ModelFileParts.ReadRig(reader);
    }

    /// <summary>
    /// Reads only the mesh names, which sit at the very front of the file, so a game can pick
    /// meshes without reading the rig or the geometry. <paramref name="name"/> is only for messages.
    /// </summary>
    public static string[] ReadMeshNames(Stream stream, string name)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        ReadHeader(reader, name);
        return ModelFileParts.ReadNames(reader);
    }

    /// <summary>Writes the whole file. <see cref="ModelData.ToBytes"/> checks the model first.</summary>
    internal static void WriteTo(BinaryWriter writer, ModelData model, Func<TextureData, string>? share)
    {
        writer.Write(Magic);
        writer.Write(Version);

        // Part names, then the rig, come first, so a game can read either without the geometry and textures.
        ModelFileParts.WriteNames(writer, model.MeshNames);
        ModelFileParts.WriteRig(writer, model.Skeleton, model.Clips);
        ModelFileParts.WriteArray(writer, model.Vertices);
        ModelFileParts.WriteArray(writer, model.Indices);
        ModelFileParts.WriteArray(writer, model.Parts);
        ModelFileParts.WriteArray(writer, model.Materials);
        ModelFileParts.WriteShaders(writer, model.MaterialShaders);
        ModelFileParts.WriteMasks(writer, model.MaterialMasks);
        ModelFileParts.WriteTextures(writer, model.Textures, share);
        WriteSections(writer, model);
    }

    // The optional sections after the textures, each only when a material has what it holds, so a model without them
    // writes nothing here.
    private static void WriteSections(BinaryWriter writer, ModelData model)
    {
        if (model.MaterialOcclusion.Length != 0)
        {
            writer.Write(OcclusionSection);
            ModelFileParts.WriteArray(writer, model.MaterialOcclusion);
        }

        if (model.MaterialToon.Length != 0)
        {
            writer.Write(ToonSection);
            ModelFileParts.WriteArray(writer, model.MaterialToon.Select(toon => toon is { } t ? new StoredToon(t.Steps, t.Ramp, t.Rim) : default).ToArray());
        }
    }

    // The optional sections, until the stream ends.
    private static (int[] Occlusion, ModelMaterialToon?[] Toon) ReadSections(Stream stream, BinaryReader reader, string name)
    {
        int[] occlusion = [];
        ModelMaterialToon?[] toon = [];
        for (int section = stream.ReadByte(); section != -1; section = stream.ReadByte())
        {
            if (section == OcclusionSection)
            {
                occlusion = ModelFileParts.ReadArray<int>(reader);
            }
            else if (section == ToonSection)
            {
                toon = [.. ModelFileParts.ReadArray<StoredToon>(reader).Select(s => s.Steps > 0 ? new ModelMaterialToon(s.Steps, s.Ramp, s.Rim) : (ModelMaterialToon?)null)];
            }
            else
            {
                throw new InvalidDataException($"{name} is damaged: it has the unknown section {section} after its textures. Cook it again.");
            }
        }

        return (occlusion, toon);
    }

    // A material's toon settings as the file keeps them, with 0 steps for a material that isn't toon.
    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct StoredToon(int Steps, int Ramp, Vector3 Rim);

    private static void ReadHeader(BinaryReader reader, string name)
    {
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
        {
            throw new InvalidDataException($"{name} is not a current model file. Cook it again.");
        }
    }
}
