using System.Buffers.Binary;
using BCnEncoder.Decoder;
using BCnEncoder.Shared;
using Rendition;

namespace Super;

/// <summary>
/// A texture as the cooker stores it: a small header, then its levels. Everything is little-endian: magic "STEX",
/// version, width, height, kind, a normal-map byte, the <see cref="TextureFormat"/>, the number of levels, then each
/// level's bytes, largest first, each as <see cref="TextureData.LevelSize"/> says. An RGBA8 texture has one level, the
/// full-size pixels, and the game builds its mips as it loads it. A block-compressed one has its whole mip chain, made
/// when it was cooked, since the game can't filter blocks.
/// </summary>
internal sealed record CookedTexture(int Width, int Height, TextureKind Kind, bool NormalMap, TextureFormat Format, byte[][] Mips)
{
    private const uint Magic = 0x58455453;

    // 2: the format and every level, so a texture can be cooked block-compressed with its mips.
    private const int Version = 2;
    private const int HeaderSize = 4 + 4 + 4 + 4 + 4 + 1 + 4 + 4;

    /// <summary>An uncompressed texture: its full-size RGBA8 pixels, rows from the top.</summary>
    public CookedTexture(int width, int height, TextureKind kind, bool normalMap, byte[] pixels)
        : this(width, height, kind, normalMap, TextureFormat.Rgba8, [pixels])
    {
    }

    /// <summary>A texture the cooker built the mips of, such as a block-compressed one.</summary>
    public static CookedTexture From(TextureData texture, bool normalMap) =>
        new(texture.Width, texture.Height, texture.Kind, normalMap, texture.Format, texture.Mips);

    public byte[] Write()
    {
        byte[] bytes = new byte[HeaderSize + Mips.Sum(mip => mip.Length)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, Magic);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), Version);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), Width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), Height);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), (int)Kind);
        bytes[20] = NormalMap ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(21), (int)Format);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(25), Mips.Length);
        int at = HeaderSize;
        foreach (byte[] mip in Mips)
        {
            mip.CopyTo(bytes, at);
            at += mip.Length;
        }

        return bytes;
    }

    public static CookedTexture Read(byte[] bytes, string name)
    {
        var (width, height) = ReadSize(bytes, name);
        var kind = (TextureKind)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
        var format = (TextureFormat)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(21));
        var mips = new byte[BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(25))][];
        int at = HeaderSize;
        for (int level = 0; level < mips.Length; level++)
        {
            int size = TextureData.LevelSize(format, Math.Max(width >> level, 1), Math.Max(height >> level, 1));
            mips[level] = bytes[at..(at + size)];
            at += size;
        }

        return new CookedTexture(width, height, kind, bytes[20] != 0, format, mips);
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
        var format = (TextureFormat)BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(21));
        int levels = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(25));
        // Half floats are only for a lightmap straight from the baker; a cook never writes them.
        if (width < 1 || height < 1 || !Enum.IsDefined(format) || format == TextureFormat.Rgba16f
            || levels != (format == TextureFormat.Rgba8 ? 1 : LevelCount(width, height))
            || Length(format, width, height, levels) != bytes.Length - HeaderSize)
        {
            throw Stale(name);
        }

        return (width, height);
    }

    /// <summary>
    /// The texture as the renderer takes it, with its whole mip chain: an RGBA8 one's are built
    /// here, a normal map's normals brought back to unit length, and a compressed one's are read as they are.
    /// </summary>
    public TextureData ToTextureData() => Format == TextureFormat.Rgba8
        ? TextureData.FromPixels(Width, Height, Kind, Mips[0], NormalMap)
        : new TextureData { Width = Width, Height = Height, Kind = Kind, Format = Format, Mips = Mips };

    /// <summary>
    /// The full-size level as RGBA8 pixels, rows from the top. A compressed texture is decoded, so its pixels
    /// are close to the ones it was cooked from, not the same. A BC4 texture's red is copied to green and blue,
    /// and a normal map's blue is rebuilt from red and green, as the shaders rebuild it.
    /// </summary>
    public byte[] DecodePixels()
    {
        if (Format == TextureFormat.Rgba8)
        {
            return Mips[0];
        }

        if (Format == TextureFormat.Bc6h)
        {
            throw new NotSupportedException("A BC6H texture holds light brighter than RGBA8 can show, so it can't be decoded to RGBA8 pixels.");
        }

        var colors = new BcDecoder().DecodeRaw(Mips[0], Width, Height, Format switch
        {
            TextureFormat.Bc7 => CompressionFormat.Bc7,
            TextureFormat.Bc5 => CompressionFormat.Bc5,
            _ => CompressionFormat.Bc4,
        });

        byte[] pixels = new byte[Width * Height * 4];
        for (int i = 0; i < colors.Length; i++)
        {
            var color = colors[i];
            pixels[i * 4] = color.r;
            pixels[i * 4 + 1] = Format == TextureFormat.Bc4 ? color.r : color.g;
            pixels[i * 4 + 2] = Format switch
            {
                TextureFormat.Bc4 => color.r,
                TextureFormat.Bc5 => NormalMap ? NormalZ(color.r, color.g) : (byte)0,
                _ => color.b,
            };
            pixels[i * 4 + 3] = Format == TextureFormat.Bc7 ? color.a : (byte)255;
        }

        return pixels;
    }

    // The third axis of a unit normal stored as bytes, from the other two.
    private static byte NormalZ(byte red, byte green)
    {
        float x = red / 255f * 2f - 1f;
        float y = green / 255f * 2f - 1f;
        float z = MathF.Sqrt(MathF.Max(0f, 1f - x * x - y * y));
        return (byte)MathF.Round((z * 0.5f + 0.5f) * 255f);
    }

    private static int LevelCount(int width, int height) => 32 - int.LeadingZeroCount(Math.Max(width, height));

    private static long Length(TextureFormat format, int width, int height, int levels)
    {
        long length = 0;
        for (int level = 0; level < levels; level++)
        {
            length += TextureData.LevelSize(format, Math.Max(width >> level, 1), Math.Max(height >> level, 1));
        }

        return length;
    }

    private static ContentException Stale(string name)
    {
        return new ContentException([$"{name} is not a current cooked texture. Cook again."]);
    }
}
