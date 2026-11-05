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
    public MaterialFlags Flags;
    public float Ior;
    public Subsurface Subsurface;
}

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
    private const int Version = 5; // 4: materials gained Subsurface. 5: materials gained Ior.

    public required Vertex[] Vertices { get; init; }
    public required uint[] Indices { get; init; }
    public required ModelPart[] Parts { get; init; }
    public required ModelMaterial[] Materials { get; init; }
    public required TextureData[] Textures { get; init; }

    /// <summary>The joints the vertices follow, for a rigged model. Null for a static one.</summary>
    public Skeleton? Skeleton { get; init; }

    /// <summary>Motions for the skeleton, each with as many joints as it has.</summary>
    public AnimationClip[] Clips { get; init; } = [];

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

    /// <summary>The bytes of a <c>.rmodel</c> file, as <see cref="Write"/> stores them.</summary>
    public byte[] ToBytes()
    {
        foreach (var clip in Clips)
        {
            if (Skeleton is null || clip.JointCount != Skeleton.Count)
            {
                throw new InvalidOperationException(
                    $"Clip {clip.Name} has {clip.JointCount} joints, but the model has {Skeleton?.Count ?? 0}.");
            }
        }

        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            WriteTo(writer);
        }

        return stream.ToArray();
    }

    private void WriteTo(BinaryWriter writer)
    {
        writer.Write(Magic);
        writer.Write(Version);

        // The rig comes first, so a game can read it without the geometry and textures behind it.
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
        using var stream = new BufferedStream(File.OpenRead(path), 1 << 20);
        return Read(stream, path);
    }

    /// <summary>Reads a model from <paramref name="stream"/>, which is left open. <paramref name="name"/> is only for messages.</summary>
    public static ModelData Read(Stream stream, string name)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var (skeleton, clips) = ReadRig(reader, name);
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
            Skeleton = skeleton,
            Clips = clips,
        };
    }

    /// <summary>Reads only the skeleton and clips, which sit at the front of the file, so it's quick.</summary>
    public static (Skeleton? Skeleton, AnimationClip[] Clips) ReadRig(string path)
    {
        using var stream = new BufferedStream(File.OpenRead(path), 1 << 16);
        return ReadRig(stream, path);
    }

    /// <summary>Reads only the skeleton and clips from <paramref name="stream"/>, which is left open. <paramref name="name"/> is only for messages.</summary>
    public static (Skeleton? Skeleton, AnimationClip[] Clips) ReadRig(Stream stream, string name)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        return ReadRig(reader, name);
    }

    private static (Skeleton? Skeleton, AnimationClip[] Clips) ReadRig(BinaryReader reader, string name)
    {
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
        {
            throw new InvalidDataException($"{name} is not a current model file. Cook it again.");
        }

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
