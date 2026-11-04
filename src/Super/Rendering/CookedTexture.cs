using System.Buffers.Binary;
using Rendition;

namespace Super;

/// <summary>
/// A texture as the cooker stores it: a small header, then RGBA8 pixels. Everything is little-endian: magic "STEX", version,
/// width, height, kind, a normal-map byte, then <c>width * height * 4</c> bytes.
/// </summary>
internal sealed record CookedTexture(int Width, int Height, TextureKind Kind, bool NormalMap, byte[] Pixels)
{
    private const uint Magic = 0x58455453;
    private const int Version = 1;
    private const int HeaderSize = 4 + 4 + 4 + 4 + 4 + 1;

    public byte[] Write()
    {
        byte[] bytes = new byte[HeaderSize + Pixels.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), Width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), Height);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), (int)Kind);
        bytes[20] = NormalMap ? (byte)1 : (byte)0;
        Pixels.CopyTo(bytes, HeaderSize);
        return bytes;
    }

    public static CookedTexture Read(byte[] bytes, string name)
    {
        var (width, height) = ReadSize(bytes, name);
        var kind = (TextureKind)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
        return new CookedTexture(width, height, kind, bytes[20] != 0, bytes[HeaderSize..]);
    }

    public static (int Width, int Height) ReadSize(byte[] bytes, string name)
    {
        if (bytes.Length < HeaderSize
            || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != Magic
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) != Version)
        {
            throw Stale(name);
        }

        int width = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        int height = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        if (width < 0 || height < 0 || (long)width * height * 4 != bytes.Length - HeaderSize)
        {
            throw Stale(name);
        }

        return (width, height);
    }

    private static ContentException Stale(string name)
    {
        return new ContentException([$"{name} is not a current cooked texture. Cook again."]);
    }
}
