using System.IO.Compression;

namespace Rendition;

/// <summary>
/// The parts of the .lightbake format that don't need the whole file: counted lists and
/// Brotli-compressed pages. Each read checks what it reads against the bytes left, so a damaged file throws
/// <see cref="InvalidDataException"/> instead of asking for gigabytes.
/// </summary>
internal static class LightBakeBinary
{
    // A count read from the file is checked against the bytes left, so a damaged count can't ask for gigabytes.
    public static int ReadCount(BinaryReader reader, MemoryStream stream, int minBytesEach, string what)
    {
        var count = reader.ReadInt32();
        var left = stream.Length - stream.Position;
        if (count < 0 || (long)count * minBytesEach > left)
        {
            throw new InvalidDataException(
                $"The .lightbake file is cut off or damaged: it says {count} {what} but only {left} bytes are left.");
        }

        return count;
    }

    // A count, checked like ReadCount, then each item.
    public static List<T> ReadList<T>(
        BinaryReader reader, MemoryStream stream, int minBytesEach, string what, Func<BinaryReader, T> read)
    {
        var count = ReadCount(reader, stream, minBytesEach, what);
        var items = new List<T>(count);
        for (var i = 0; i < count; i++)
        {
            items.Add(read(reader));
        }

        return items;
    }

    // A count, then each item.
    public static void WriteList<T>(BinaryWriter writer, IReadOnlyList<T> items, Action<BinaryWriter, T> write)
    {
        writer.Write(items.Count);
        foreach (var item in items)
        {
            write(writer, item);
        }
    }

    public static void WritePage(BinaryWriter writer, LightBakePage page)
    {
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            brotli.Write(page.Pixels);
        }

        writer.Write(page.Width);
        writer.Write(page.Height);
        writer.Write((byte)page.Format);
        writer.Write(page.Pixels.Length);
        writer.Write((int)compressed.Length);
        writer.Write(compressed.GetBuffer(), 0, (int)compressed.Length);
    }

    // largest is the most texels a side the page may have, which what names in the message.
    public static LightBakePage ReadPage(BinaryReader reader, MemoryStream stream, int largest, string what)
    {
        var header = new PageHeader(
            reader.ReadInt32(), reader.ReadInt32(), reader.ReadByte(), reader.ReadInt32(), reader.ReadInt32());
        header.CheckLengths(stream.Length - stream.Position);
        var format = header.CheckFormat();
        header.CheckSize(format, largest, what);

        var compressed = reader.ReadBytes(header.CompressedLength);
        return new LightBakePage(header.Width, header.Height, format, Inflate(compressed, header.RawLength));
    }

    private static byte[] Inflate(byte[] compressed, int rawLength)
    {
        var pixels = new byte[rawLength];
        bool extra;
        try
        {
            using var brotli = new BrotliStream(new MemoryStream(compressed), CompressionMode.Decompress);
            brotli.ReadExactly(pixels);
            extra = brotli.ReadByte() >= 0;
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException)
        {
            throw new InvalidDataException($"The .lightbake page's pixels can't be inflated: {e.Message}", e);
        }

        if (extra)
        {
            throw new InvalidDataException(
                $"The .lightbake page is damaged: its pixels inflate to more than the {rawLength} bytes it says.");
        }

        return pixels;
    }

    // Bytes a page of this size and format takes: 8 or 4 a texel, or 16 a 4 by 4 block for the block formats.
    private static long ExpectedSize(int width, int height, LightBakePageFormat format)
    {
        switch (format)
        {
            case LightBakePageFormat.Rgba16f:
                return (long)width * height * 8;
            case LightBakePageFormat.Rgba8:
                return (long)width * height * 4;
            default:
                return (long)((width + 3) / 4) * ((height + 3) / 4) * 16;
        }
    }

    // The fields in front of a page's compressed pixels, checked in the order the file lists them.
    private readonly record struct PageHeader(int Width, int Height, byte Format, int RawLength, int CompressedLength)
    {
        public void CheckLengths(long left)
        {
            if (RawLength < 0 || CompressedLength < 0 || CompressedLength > left)
            {
                throw new InvalidDataException(
                    $"The .lightbake file is cut off or damaged: a page says {CompressedLength} compressed bytes " +
                    $"and {RawLength} raw bytes but {left} bytes are left.");
            }
        }

        public LightBakePageFormat CheckFormat()
        {
            if (!Enum.IsDefined((LightBakePageFormat)Format))
            {
                throw new InvalidDataException($"The .lightbake page format is wrong: found {Format}, expected 0 to 3.");
            }

            return (LightBakePageFormat)Format;
        }

        public void CheckSize(LightBakePageFormat format, int largest, string what)
        {
            if (Width < 0 || Height < 0)
            {
                throw new InvalidDataException(
                    $"The .lightbake page is damaged: it is {Width} by {Height} texels, and a size can't be negative.");
            }

            // Checked before the pixels are allocated, so a damaged size can't ask for gigabytes.
            if (Width > largest || Height > largest)
            {
                throw new InvalidDataException(
                    $"The .lightbake page is damaged: it is {Width} by {Height} texels, but a {what} is at most {largest} a side.");
            }

            var expected = ExpectedSize(Width, Height, format);
            if (RawLength != expected)
            {
                throw new InvalidDataException(
                    $"The .lightbake page is damaged: a {Width} by {Height} {format} page takes {expected} bytes " +
                    $"but the file says {RawLength}.");
            }
        }
    }
}
