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

/// <summary>An RGBA8 texture with a full mip chain, largest level first.</summary>
public sealed class TextureData
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required TextureKind Kind { get; init; }
    public required byte[][] Mips { get; init; }
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
    public MaterialFlags Flags;
}

/// <summary>One run of triangles sharing a material.</summary>
public struct ModelPart
{
    public uint FirstIndex;
    public uint IndexCount;
    public int Material;
}

/// <summary>The cooked form of a model, produced by tools/Rendition.Import.</summary>
public sealed class ModelData
{
    private const uint Magic = 0x4C444D52; // "RMDL"
    private const int Version = 1;

    public required Vertex[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
    public required ModelPart[] Parts { get; init; }
    public required ModelMaterial[] Materials { get; init; }
    public required TextureData[] Textures { get; init; }

    public void Write(string path)
    {
        using var writer = new BinaryWriter(File.Create(path), Encoding.UTF8);
        writer.Write(Magic);
        writer.Write(Version);
        WriteArray(writer, Vertices);
        WriteArray(writer, Indices);
        WriteArray(writer, Parts);
        WriteArray(writer, Materials);
        writer.Write(Textures.Length);
        foreach (var texture in Textures)
        {
            writer.Write(texture.Width);
            writer.Write(texture.Height);
            writer.Write((int)texture.Kind);
            writer.Write(texture.Mips.Length);
            foreach (byte[] mip in texture.Mips)
            {
                WriteArray(writer, mip);
            }
        }
    }

    public static ModelData Read(string path)
    {
        using var reader = new BinaryReader(new BufferedStream(File.OpenRead(path), 1 << 20), Encoding.UTF8);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
        {
            throw new InvalidDataException($"{path} is not a current model file. Re-run the importer.");
        }

        var vertices = ReadArray<Vertex>(reader);
        var indices = ReadArray<uint>(reader);
        var parts = ReadArray<ModelPart>(reader);
        var materials = ReadArray<ModelMaterial>(reader);
        var textures = new TextureData[reader.ReadInt32()];
        for (int i = 0; i < textures.Length; i++)
        {
            int width = reader.ReadInt32();
            int height = reader.ReadInt32();
            var kind = (TextureKind)reader.ReadInt32();
            var mips = new byte[reader.ReadInt32()][];
            for (int mip = 0; mip < mips.Length; mip++)
            {
                mips[mip] = ReadArray<byte>(reader);
            }

            textures[i] = new TextureData { Width = width, Height = height, Kind = kind, Mips = mips };
        }

        return new ModelData
        {
            Vertices = vertices,
            Indices = indices,
            Parts = parts,
            Materials = materials,
            Textures = textures,
        };
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
