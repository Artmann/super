namespace Rendition;

/// <summary>How a texture's bytes are stored.</summary>
public enum TextureFormat
{
    /// <summary>Four bytes a pixel: red, green, blue and alpha.</summary>
    Rgba8,

    /// <summary>
    /// Blocks of 4x4 pixels in 16 bytes, with all four channels: a quarter of
    /// <see cref="Rgba8"/>'s size, and close to it for photos and painted textures.
    /// </summary>
    Bc7,

    /// <summary>
    /// Blocks of 4x4 pixels in 16 bytes, with red and green only, each kept as well as
    /// <see cref="Bc4"/> keeps one. For normal maps, whose third axis follows from the other two.
    /// </summary>
    Bc5,

    /// <summary>Blocks of 4x4 pixels in 8 bytes, with red only. For height maps.</summary>
    Bc4,

    /// <summary>
    /// Blocks of 4x4 pixels in 16 bytes, with red, green and blue as unsigned half floats, so light brighter than 1
    /// keeps its brightness. For baked lightmaps. Only the GPU reads it: <see cref="TextureKind"/> doesn't apply, as
    /// it is always linear.
    /// </summary>
    Bc6h,

    /// <summary>
    /// Eight bytes a pixel: red, green, blue and alpha as half floats, uncompressed. For a lightmap straight from the
    /// baker, before it is cooked to <see cref="Bc6h"/>. Like that, it is always linear, and cooked textures never use it.
    /// </summary>
    Rgba16f,
}

/// <summary>A texture with a full mip chain, largest level first, in RGBA8 unless <see cref="Format"/> says otherwise.</summary>
public sealed class TextureData
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required TextureKind Kind { get; init; }
    public required byte[][] Mips { get; init; }
    public TextureFormat Format { get; init; } = TextureFormat.Rgba8;

    private const uint Magic = 0x58455452; // "RTEX"
    private const int Version = 2; // 2: textures say their format.

    /// <summary>The bytes of the texture and its mips, as <see cref="Read"/> reads them.</summary>
    public byte[] ToBytes()
    {
        var stream = new MemoryStream(Mips.Sum(mip => mip.Length) + 64);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            WriteBody(writer);
        }

        return stream.ToArray();
    }

    /// <summary>Reads a texture written by <see cref="ToBytes"/>. <paramref name="name"/> is only for messages.</summary>
    public static TextureData Read(Stream stream, string name)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
        {
            throw new InvalidDataException($"{name} is not a current texture file. Cook it again.");
        }

        return ReadBody(reader);
    }

    /// <summary>Writes the size, kind, format and mips, without a header, for a texture inside another file.</summary>
    internal void WriteBody(BinaryWriter writer)
    {
        writer.Write(Width);
        writer.Write(Height);
        writer.Write((int)Kind);
        writer.Write((int)Format);
        writer.Write(Mips.Length);
        foreach (byte[] mip in Mips)
        {
            writer.Write(mip.Length);
            writer.Write(mip);
        }
    }

    internal static TextureData ReadBody(BinaryReader reader)
    {
        int width = reader.ReadInt32();
        int height = reader.ReadInt32();
        var kind = (TextureKind)reader.ReadInt32();
        var format = (TextureFormat)reader.ReadInt32();
        var mips = new byte[reader.ReadInt32()][];
        for (int i = 0; i < mips.Length; i++)
        {
            mips[i] = new byte[reader.ReadInt32()];
            reader.BaseStream.ReadExactly(mips[i]);
        }

        return new TextureData { Width = width, Height = height, Kind = kind, Format = format, Mips = mips };
    }

    /// <summary>How many bytes a level of <paramref name="width"/> by <paramref name="height"/> pixels takes in <paramref name="format"/>.</summary>
    public static int LevelSize(TextureFormat format, int width, int height)
    {
        int blocks = (width + 3) / 4 * ((height + 3) / 4);
        return format switch
        {
            TextureFormat.Rgba8 => width * height * 4,
            TextureFormat.Rgba16f => width * height * 8,
            TextureFormat.Bc4 => blocks * 8,
            _ => blocks * 16,
        };
    }

    /// <summary>The lowest and highest alpha of the full-size image, from 0 to 1. Only for <see cref="TextureFormat.Rgba8"/>.</summary>
    public (float Lowest, float Highest) AlphaRange()
    {
        if (Format != TextureFormat.Rgba8)
        {
            throw new InvalidOperationException($"Can't read the alpha of a {Format} texture, only of an RGBA8 one.");
        }

        byte lowest = 255;
        byte highest = 0;
        byte[] pixels = Mips[0];
        for (int i = 3; i < pixels.Length; i += 4)
        {
            lowest = Math.Min(lowest, pixels[i]);
            highest = Math.Max(highest, pixels[i]);
        }

        return (lowest / 255f, highest / 255f);
    }

    /// <summary>
    /// Builds the mip chain for an image's RGBA bytes, row by row. Each level is filtered from
    /// the one above with a Kaiser-windowed sinc, which keeps detail crisp where a plain average
    /// of four pixels would soften it a little more at every level. Colour is filtered in linear
    /// light, the edges wrap round as a tiling texture's do, and a normal map's normals are
    /// brought back to unit length.
    /// </summary>
    public static TextureData FromPixels(int width, int height, TextureKind kind, byte[] rgba, bool normalMap = false)
    {
        if (width < 1 || height < 1)
        {
            throw new ArgumentException($"A texture can't be {width}x{height}.", nameof(width));
        }

        if (rgba.Length != width * height * 4)
        {
            throw new ArgumentException(
                $"A {width}x{height} texture needs {width * height * 4} bytes, not {rgba.Length}.", nameof(rgba));
        }

        return new TextureData { Width = width, Height = height, Kind = kind, Mips = MipChain.Build(rgba, width, height, kind, normalMap) };
    }
}
