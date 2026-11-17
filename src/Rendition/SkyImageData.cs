using System.Numerics;

namespace Rendition;

/// <summary>
/// A photographed sky, cooked from an equirectangular HDRI: a cube of six half-float faces with their mips, and the sun
/// the cooker took out of them. The faces keep the sky and the ground without the sun, so the light and the rough
/// reflections never see it; the renderer draws it back as a disc where it was, in the background and the sharpest
/// reflection, as it does a physical sky's. See "A photographed sky" in docs/rendering.md.
/// </summary>
public sealed class SkyImageData
{
    private const uint Magic = 0x594B5352; // "RSKY"
    private const int Version = 1;

    /// <summary>
    /// The six faces, in Vulkan's order (+X, -X, +Y, -Y, +Z, -Z), each <see cref="TextureFormat.Rgba16f"/> with a full
    /// mip chain, laid out as <see cref="FaceDirection"/> says.
    /// </summary>
    public required TextureData[] Faces { get; init; }

    /// <summary>Towards the sun, as the image was photographed; zero when the image has no sun, as an overcast one hasn't.</summary>
    public Vector3 SunDirection { get; init; }

    /// <summary>How bright the sun's disc is, so it gives back the light taken out of the faces.</summary>
    public Vector3 SunRadiance { get; init; }

    /// <summary>The cosine of the sun's angular radius.</summary>
    public float SunCosine { get; init; } = 1f;

    /// <summary>
    /// The direction through a point on a face, from <paramref name="uv"/> -1 at its left and top edges to 1 at its
    /// right and bottom, not normalised. It matches how Vulkan samples a cube, and cubeDirection in the environment shaders.
    /// </summary>
    public static Vector3 FaceDirection(int face, Vector2 uv) => face switch
    {
        0 => new Vector3(1f, -uv.Y, -uv.X),
        1 => new Vector3(-1f, -uv.Y, uv.X),
        2 => new Vector3(uv.X, 1f, uv.Y),
        3 => new Vector3(uv.X, -1f, -uv.Y),
        4 => new Vector3(uv.X, -uv.Y, 1f),
        5 => new Vector3(-uv.X, -uv.Y, -1f),
        _ => throw new ArgumentOutOfRangeException(nameof(face), face, "A cube has faces 0 to 5."),
    };

    /// <summary>The bytes of the sky, as <see cref="Read"/> reads them.</summary>
    public byte[] ToBytes()
    {
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            WriteVector(writer, SunDirection);
            WriteVector(writer, SunRadiance);
            writer.Write(SunCosine);
            writer.Write(Faces.Length);
            foreach (var face in Faces)
            {
                face.WriteBody(writer);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Reads a sky written by <see cref="ToBytes"/>. <paramref name="name"/> is only for messages.</summary>
    public static SkyImageData Read(Stream stream, string name)
    {
        var (direction, radiance, cosine, reader) = ReadHeader(stream, name);
        using (reader)
        {
            var faces = new TextureData[reader.ReadInt32()];
            if (faces.Length != 6)
            {
                throw new InvalidDataException($"{name} has {faces.Length} faces, not 6. Cook it again.");
            }

            for (int i = 0; i < faces.Length; i++)
            {
                faces[i] = TextureData.ReadBody(reader);
            }

            return new SkyImageData { Faces = faces, SunDirection = direction, SunRadiance = radiance, SunCosine = cosine };
        }
    }

    /// <summary>Reads only where the sun is, without the faces, from the start of a sky's bytes.</summary>
    public static Vector3 ReadSunDirection(Stream stream, string name)
    {
        var (direction, _, _, reader) = ReadHeader(stream, name);
        reader.Dispose();
        return direction;
    }

    private static (Vector3 Direction, Vector3 Radiance, float Cosine, BinaryReader Reader) ReadHeader(Stream stream, string name)
    {
        var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
        {
            reader.Dispose();
            throw new InvalidDataException($"{name} is not a current sky file. Cook it again.");
        }

        var direction = ReadVector(reader);
        var radiance = ReadVector(reader);
        return (direction, radiance, reader.ReadSingle(), reader);
    }

    private static void WriteVector(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector(BinaryReader reader) => new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}
