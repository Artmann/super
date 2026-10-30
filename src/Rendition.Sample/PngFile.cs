using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

/// <summary>Writes RGBA pixels as a PNG. A sample only needs this for screenshots.</summary>
internal static class PngFile
{
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static void Write(string path, byte[] rgba, int width, int height)
    {
        using var file = File.Create(path);
        file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // Bits per channel.
        header[9] = 6; // RGBA.
        WriteChunk(file, "IHDR", header);

        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        {
            int rowSize = width * 4;
            for (int row = 0; row < height; row++)
            {
                zlib.WriteByte(0); // Each row starts with its filter, and 0 means none.
                zlib.Write(rgba, row * rowSize, rowSize);
            }
        }

        WriteChunk(file, "IDAT", compressed.ToArray());
        WriteChunk(file, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        stream.Write(number);

        byte[] name = Encoding.ASCII.GetBytes(type);
        stream.Write(name);
        stream.Write(data);

        uint crc = Update(Update(0xFFFFFFFF, name), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(number, crc);
        stream.Write(number);
    }

    private static uint Update(uint crc, byte[] data)
    {
        foreach (byte value in data)
        {
            crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            }

            table[i] = value;
        }

        return table;
    }
}
