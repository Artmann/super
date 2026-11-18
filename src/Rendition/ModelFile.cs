using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>Writes and reads the pieces of a <c>.rmodel</c> file. <see cref="ModelData"/> puts them in order.</summary>
internal static class ModelFile
{
    private const byte TextureInside = 0;
    private const byte TextureByHash = 1;

    public static void WriteArray<T>(BinaryWriter writer, T[] items) where T : unmanaged
    {
        writer.Write(items.Length);
        writer.Write(MemoryMarshal.AsBytes(items.AsSpan()));
    }

    public static T[] ReadArray<T>(BinaryReader reader) where T : unmanaged
    {
        var items = new T[reader.ReadInt32()];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(items.AsSpan()));
        return items;
    }

    public static void WriteNames(BinaryWriter writer, string[] names)
    {
        writer.Write(names.Length);
        foreach (string name in names)
        {
            writer.Write(name);
        }
    }

    public static string[] ReadNames(BinaryReader reader)
    {
        var names = new string[reader.ReadInt32()];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = reader.ReadString();
        }

        return names;
    }

    public static void WriteRig(BinaryWriter writer, Skeleton? skeleton, AnimationClip[] clips)
    {
        writer.Write(skeleton is not null);
        if (skeleton is not null)
        {
            WriteNames(writer, skeleton.Names);
            WriteArray(writer, skeleton.Parents);
            WriteArray(writer, skeleton.Rest);
            WriteArray(writer, skeleton.InverseBind);
            WriteArray(writer, new[] { skeleton.RootTransform });
            WriteArray(writer, skeleton.Bounds ?? []);
        }

        writer.Write(clips.Length);
        foreach (var clip in clips)
        {
            clip.Write(writer);
        }
    }

    public static (Skeleton? Skeleton, AnimationClip[] Clips) ReadRig(BinaryReader reader)
    {
        Skeleton? skeleton = null;
        if (reader.ReadBoolean())
        {
            var names = ReadNames(reader);
            var parents = ReadArray<int>(reader);
            var rest = ReadArray<JointPose>(reader);
            var inverseBind = ReadArray<Matrix4x4>(reader);
            var root = ReadArray<Matrix4x4>(reader)[0];
            var bounds = ReadArray<JointBounds>(reader);
            skeleton = new Skeleton(names, parents, rest, inverseBind, root, bounds.Length == names.Length ? bounds : null);
        }

        var clips = new AnimationClip[reader.ReadInt32()];
        for (int i = 0; i < clips.Length; i++)
        {
            clips[i] = AnimationClip.Read(reader);
        }

        return (skeleton, clips);
    }

    public static void WriteShaders(BinaryWriter writer, ModelMaterialShader?[] shaders)
    {
        writer.Write(shaders.Length);
        foreach (var shader in shaders)
        {
            writer.Write(shader is not null);
            if (shader is not null)
            {
                writer.Write(shader.Name);
                WriteArray(writer, shader.Parameters);
                WriteNames(writer, shader.Textures);
            }
        }
    }

    public static ModelMaterialShader?[] ReadShaders(BinaryReader reader)
    {
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

        return shaders;
    }

    public static void WriteMasks(BinaryWriter writer, ModelMaterialMasks?[] materialMasks)
    {
        writer.Write(materialMasks.Length);
        foreach (var masks in materialMasks)
        {
            writer.Write(masks is not null);
            if (masks is not null)
            {
                writer.Write(masks.Texture);
                WriteArray(writer, new[] { masks.Wear, masks.Dirt });
            }
        }
    }

    public static ModelMaterialMasks?[] ReadMasks(BinaryReader reader)
    {
        var masks = new ModelMaterialMasks?[reader.ReadInt32()];
        for (int i = 0; i < masks.Length; i++)
        {
            if (reader.ReadBoolean())
            {
                int maskTexture = reader.ReadInt32();
                var layers = ReadArray<MaterialLayer>(reader);
                masks[i] = new ModelMaterialMasks(maskTexture, layers[0], layers[1]);
            }
        }

        return masks;
    }

    /// <summary>Writes each texture inside the file, or, with <paramref name="share"/>, the name it is kept under.</summary>
    public static void WriteTextures(BinaryWriter writer, TextureData[] textures, Func<TextureData, string>? share)
    {
        writer.Write(textures.Length);
        foreach (var texture in textures)
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

    /// <summary>Reads the textures, finding those kept elsewhere with <paramref name="texture"/>.</summary>
    public static TextureData[] ReadTextures(BinaryReader reader, string name, Func<string, TextureData>? texture)
    {
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

        return textures;
    }
}
