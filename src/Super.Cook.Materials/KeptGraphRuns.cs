using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using Super.Materials;

namespace Super.Cook.Materials;

/// <summary>
/// A graph run as the step cache keeps it: the images the graph read with their hashes, then the packed pixels, each
/// channel as a plane of differences, Brotli compressed.
/// </summary>
internal static class KeptGraphRuns
{
    // 1: the images read, then the packed pixels, with the height's red only.
    // 2: the same, with the pixels as planes of differences, Brotli compressed.
    public const int Format = 2;

    /// <summary>
    /// How hard a kept run's pixels are compressed. Over Courtyard's runs, quality 1 on planes of differences keeps 22% of
    /// the bytes and takes about 0.15 s each way for a 2048 run, against seconds to run the graph again; higher qualities
    /// save a few percent more for several times the time.
    /// </summary>
    private const int Quality = 1;

    public static byte[] Write(PackedImages run, List<(string Path, byte[] Hash)> reads)
    {
        var stream = new MemoryStream(run.Pixels.Color.Length + 1024);
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(reads.Count);
            foreach (var (path, hash) in reads.OrderBy(read => read.Path, StringComparer.Ordinal))
            {
                writer.Write(path);
                writer.Write(hash);
            }

            writer.Write(run.Size);
            writer.Write(run.Width);
            writer.Write(run.HeightDepth);
            writer.Write(run.Pixels.NotNumbers.Count);
            foreach (string part in run.Pixels.NotNumbers)
            {
                writer.Write(part);
            }
        }

        WritePixels(stream, run.Pixels);
        return stream.ToArray();
    }

    /// <returns>The kept run, or null when an image it read has changed or is gone, or the entry doesn't read.</returns>
    public static PackedImages? Read(CookInput input, string folder, byte[] kept)
    {
        try
        {
            using var reader = new BinaryReader(new MemoryStream(kept));
            if (!ReadsUnchanged(input, folder, reader))
            {
                return null;
            }

            int size = reader.ReadInt32();
            float width = reader.ReadSingle();
            float heightDepth = reader.ReadSingle();
            var notNumbers = new string[reader.ReadInt32()];
            for (int i = 0; i < notNumbers.Length; i++)
            {
                notNumbers[i] = reader.ReadString();
            }

            if (size < Constants.MinSize || size > Constants.MaxSize || !BitOperations.IsPow2(size))
            {
                return null;
            }

            return ReadPixels(reader.BaseStream, size, notNumbers) is { } pixels ? new PackedImages(size, width, heightDepth, pixels) : null;
        }
        catch (Exception exception) when (exception is EndOfStreamException or FileNotFoundException or OverflowException
            or FormatException or InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>Whether every image the run read still has the hash it had. Reading each tracks it, as running the graph would.</summary>
    private static bool ReadsUnchanged(CookInput input, string folder, BinaryReader reader)
    {
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string path = reader.ReadString();
            byte[] hash = reader.ReadBytes(32);
            if (!SHA256.HashData(input.Read(folder + path)).AsSpan().SequenceEqual(hash))
            {
                return false;
            }
        }

        return true;
    }

    // Each channel on its own, as the difference from the pixel before, so the smooth images a graph makes turn into
    // runs of small numbers that compress far better than the pixels do.
    private static void WritePixels(Stream stream, MaterialPixels pixels)
    {
        using var brotli = new BrotliStream(stream, new BrotliCompressionOptions { Quality = Quality }, leaveOpen: true);
        var plane = new byte[pixels.Color.Length / 4];
        foreach (byte[] image in (byte[][])[pixels.Color, pixels.Normal, pixels.Surface])
        {
            for (int channel = 0; channel < 4; channel++)
            {
                WritePlane(brotli, plane, image, channel);
            }
        }

        WritePlane(brotli, plane, pixels.Height, 0);
    }

    /// <summary>The pixels <see cref="WritePixels"/> wrote, or null when they don't read or something follows them.</summary>
    private static MaterialPixels? ReadPixels(Stream stream, int size, string[] notNumbers)
    {
        int length = size * size * 4;
        var color = new byte[length];
        var normal = new byte[length];
        var surface = new byte[length];
        var height = new byte[length];
        try
        {
            using var brotli = new BrotliStream(stream, CompressionMode.Decompress, leaveOpen: true);
            var plane = new byte[length / 4];
            foreach (byte[] image in (byte[][])[color, normal, surface])
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    ReadPlane(brotli, plane, image, channel);
                }
            }

            ReadPlane(brotli, plane, height, 0);
            if (brotli.ReadByte() >= 0)
            {
                return null;
            }
        }
        catch (InvalidOperationException)
        {
            // BrotliStream says "Decoder ran into invalid data" this way, not with InvalidDataException.
            return null;
        }

        for (int i = 0; i < length; i += 4)
        {
            height[i + 1] = height[i];
            height[i + 2] = height[i];
            height[i + 3] = 255;
        }

        return new MaterialPixels(color, normal, surface, height, notNumbers);
    }

    /// <summary>Writes one channel of an RGBA image, each byte as its difference from the one before it.</summary>
    private static void WritePlane(Stream stream, byte[] plane, byte[] image, int channel)
    {
        byte previous = 0;
        for (int i = 0, at = channel; i < plane.Length; i++, at += 4)
        {
            byte value = image[at];
            plane[i] = (byte)(value - previous);
            previous = value;
        }

        stream.Write(plane);
    }

    /// <summary>Reads one channel written by <see cref="WritePlane"/> into an RGBA image.</summary>
    private static void ReadPlane(Stream stream, byte[] plane, byte[] image, int channel)
    {
        stream.ReadExactly(plane);
        byte value = 0;
        for (int i = 0, at = channel; i < plane.Length; i++, at += 4)
        {
            value += plane[i];
            image[at] = value;
        }
    }
}
