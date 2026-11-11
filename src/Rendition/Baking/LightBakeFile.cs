using System.IO.Compression;
using System.Numerics;

namespace Rendition;

/// <summary>How a <see cref="LightBakePage"/>'s pixels are stored. The baker writes the first two; the cooker rewrites pages as the others.</summary>
public enum LightBakePageFormat : byte
{
    Rgba16f = 0,
    Rgba8 = 1,
    Bc6h = 2,
    Bc7 = 3,
}

/// <summary>One image of a bake: <paramref name="Pixels"/> are the bytes of <paramref name="Format"/>, not compressed.</summary>
public sealed record LightBakePage(int Width, int Height, LightBakePageFormat Format, byte[] Pixels);

/// <summary>What a bake found out about itself, kept so a tool can show it without baking again.</summary>
/// <param name="LightsWithoutChannel">The <see cref="LocalLight.Key"/>s of Mixed lights that found no shadow mask channel.</param>
/// <param name="InvalidTexelShare">The share of lightmap texels that no ray found a front face for, 0 to 1.</param>
/// <param name="Samples">The rays shot for each texel.</param>
/// <param name="Time">How long the bake took.</param>
public sealed record LightBakeReport(
    IReadOnlyList<int> LightsWithoutChannel, float InvalidTexelShare, int Samples, TimeSpan Time);

/// <summary>
/// A baked space or chunk on disk: the four hashes it was made from, where each draw sits on the lightmap pages, the
/// pages themselves and the shadow mask channels. See "The .lightbake file" in docs/design.md.
/// </summary>
public sealed record LightBakeFile(
    BakeInputs Inputs, BakeQuality Quality,
    IReadOnlyList<(ulong BakeId, LightmapRect Rect)> Draws,
    IReadOnlyList<LightBakePage> Irradiance, IReadOnlyList<LightBakePage> Direction, IReadOnlyList<LightBakePage> Mask,
    LightBakePage? ProbeIrradiance, LightBakePage? ProbeDepth, ProbeVolumeSettings? Probes,
    IReadOnlyList<(int LightKey, int Channel)> Channels, LightBakeReport Report)
{
    public const uint Magic = 0x4B41424C; // "LBAK"
    public const int Version = 1;

    // magic 4, version 4, four hashes 32, quality 1.
    private const int HeaderSize = 41;

    private const byte HasProbeIrradiance = 1;
    private const byte HasProbeDepth = 2;
    private const byte HasProbes = 4;

    /// <summary>The file as bytes: an uncompressed header, then the rest with each page's pixels Brotli-compressed.</summary>
    public byte[] ToBytes()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(Inputs.Draws);
            writer.Write(Inputs.Lights);
            writer.Write(Inputs.Environment);
            writer.Write(Inputs.Settings);
            writer.Write((byte)Quality);

            writer.Write(Draws.Count);
            foreach (var (bakeId, rect) in Draws)
            {
                writer.Write(bakeId);
                writer.Write(rect.Page);
                writer.Write(rect.Scale.X);
                writer.Write(rect.Scale.Y);
                writer.Write(rect.Offset.X);
                writer.Write(rect.Offset.Y);
            }

            WritePages(writer, Irradiance);
            WritePages(writer, Direction);
            WritePages(writer, Mask);

            byte present = 0;
            present |= ProbeIrradiance is null ? (byte)0 : HasProbeIrradiance;
            present |= ProbeDepth is null ? (byte)0 : HasProbeDepth;
            present |= Probes is null ? (byte)0 : HasProbes;
            writer.Write(present);
            if (ProbeIrradiance is not null)
            {
                WritePage(writer, ProbeIrradiance);
            }

            if (ProbeDepth is not null)
            {
                WritePage(writer, ProbeDepth);
            }

            if (Probes is { } probes)
            {
                writer.Write(probes.Min.X);
                writer.Write(probes.Min.Y);
                writer.Write(probes.Min.Z);
                writer.Write(probes.Max.X);
                writer.Write(probes.Max.Y);
                writer.Write(probes.Max.Z);
                writer.Write(probes.Spacing);
            }

            writer.Write(Channels.Count);
            foreach (var (lightKey, channel) in Channels)
            {
                writer.Write(lightKey);
                writer.Write(channel);
            }

            writer.Write(Report.LightsWithoutChannel.Count);
            foreach (var key in Report.LightsWithoutChannel)
            {
                writer.Write(key);
            }

            writer.Write(Report.InvalidTexelShare);
            writer.Write(Report.Samples);
            writer.Write(Report.Time.Ticks);
        }

        return stream.ToArray();
    }

    /// <summary>Reads a whole file. Throws <see cref="InvalidDataException"/> for a wrong magic or version or for cut-off bytes.</summary>
    public static LightBakeFile Read(ReadOnlySpan<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream);
        try
        {
            var (inputs, quality) = ReadHeader(reader);

            var drawCount = ReadCount(reader, stream, 28, "draws");
            var draws = new List<(ulong, LightmapRect)>(drawCount);
            for (var i = 0; i < drawCount; i++)
            {
                var bakeId = reader.ReadUInt64();
                var page = reader.ReadInt32();
                var scale = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                var offset = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                draws.Add((bakeId, new LightmapRect(page, scale, offset)));
            }

            var irradiance = ReadPages(reader, stream);
            var direction = ReadPages(reader, stream);
            var mask = ReadPages(reader, stream);

            var present = reader.ReadByte();
            var probeIrradiance = (present & HasProbeIrradiance) != 0 ? ReadPage(reader, stream) : null;
            var probeDepth = (present & HasProbeDepth) != 0 ? ReadPage(reader, stream) : null;
            ProbeVolumeSettings? probes = null;
            if ((present & HasProbes) != 0)
            {
                var min = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var max = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                probes = new ProbeVolumeSettings(min, max, reader.ReadSingle());
            }

            var channelCount = ReadCount(reader, stream, 8, "channels");
            var channels = new List<(int, int)>(channelCount);
            for (var i = 0; i < channelCount; i++)
            {
                channels.Add((reader.ReadInt32(), reader.ReadInt32()));
            }

            var unassignedCount = ReadCount(reader, stream, 4, "lights without a channel");
            var unassigned = new List<int>(unassignedCount);
            for (var i = 0; i < unassignedCount; i++)
            {
                unassigned.Add(reader.ReadInt32());
            }

            var report = new LightBakeReport(
                unassigned, reader.ReadSingle(), reader.ReadInt32(), TimeSpan.FromTicks(reader.ReadInt64()));

            return new LightBakeFile(
                inputs, quality, draws, irradiance, direction, mask, probeIrradiance, probeDepth, probes, channels,
                report);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException(
                $"The .lightbake file is cut off: it ends at byte {stream.Length} before all its parts were read.");
        }
    }

    /// <summary>Reads only the header, so a freshness check never inflates a page. Throws like <see cref="Read"/> for a wrong magic or version.</summary>
    public static BakeInputs ReadInputs(ReadOnlySpan<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream);
        try
        {
            return ReadHeader(reader).Inputs;
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException(
                $"The .lightbake header is cut off: the file is {stream.Length} bytes and the header takes {HeaderSize}.");
        }
    }

    private static (BakeInputs Inputs, BakeQuality Quality) ReadHeader(BinaryReader reader)
    {
        var magic = reader.ReadUInt32();
        if (magic != Magic)
        {
            throw new InvalidDataException(
                $"The .lightbake magic is wrong: found 0x{magic:X8}, expected 0x{Magic:X8}.");
        }

        var version = reader.ReadInt32();
        if (version != Version)
        {
            throw new InvalidDataException(
                $"The .lightbake version is wrong: found {version}, expected {Version}.");
        }

        var inputs = new BakeInputs(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64());
        var quality = reader.ReadByte();
        if (!Enum.IsDefined((BakeQuality)quality))
        {
            throw new InvalidDataException($"The .lightbake quality is wrong: found {quality}, expected 0 or 1.");
        }

        return (inputs, (BakeQuality)quality);
    }

    // A count read from the file is checked against the bytes left, so a damaged count can't ask for gigabytes.
    private static int ReadCount(BinaryReader reader, MemoryStream stream, int minBytesEach, string what)
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

    private static void WritePages(BinaryWriter writer, IReadOnlyList<LightBakePage> pages)
    {
        writer.Write(pages.Count);
        foreach (var page in pages)
        {
            WritePage(writer, page);
        }
    }

    private static void WritePage(BinaryWriter writer, LightBakePage page)
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

    private static List<LightBakePage> ReadPages(BinaryReader reader, MemoryStream stream)
    {
        var count = ReadCount(reader, stream, 17, "pages");
        var pages = new List<LightBakePage>(count);
        for (var i = 0; i < count; i++)
        {
            pages.Add(ReadPage(reader, stream));
        }

        return pages;
    }

    private static LightBakePage ReadPage(BinaryReader reader, MemoryStream stream)
    {
        var width = reader.ReadInt32();
        var height = reader.ReadInt32();
        var format = reader.ReadByte();
        var rawLength = reader.ReadInt32();
        var compressedLength = reader.ReadInt32();
        var left = stream.Length - stream.Position;
        if (rawLength < 0 || compressedLength < 0 || compressedLength > left)
        {
            throw new InvalidDataException(
                $"The .lightbake file is cut off or damaged: a page says {compressedLength} compressed bytes " +
                $"and {rawLength} raw bytes but {left} bytes are left.");
        }

        if (!Enum.IsDefined((LightBakePageFormat)format))
        {
            throw new InvalidDataException($"The .lightbake page format is wrong: found {format}, expected 0 to 3.");
        }

        var compressed = reader.ReadBytes(compressedLength);
        var pixels = new byte[rawLength];
        try
        {
            using var brotli = new BrotliStream(new MemoryStream(compressed), CompressionMode.Decompress);
            brotli.ReadExactly(pixels);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException or IOException)
        {
            throw new InvalidDataException($"The .lightbake page's pixels can't be inflated: {e.Message}", e);
        }

        return new LightBakePage(width, height, (LightBakePageFormat)format, pixels);
    }
}
