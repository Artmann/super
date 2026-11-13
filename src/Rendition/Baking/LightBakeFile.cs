using System.IO.Compression;
using System.Numerics;
using Rendition.Vulkan;

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
/// A chunk's transfer: how much sky and sun light reaches each lightmap texel, as the four L1 spherical harmonic
/// coefficients (see <see cref="ShL1"/>) in each texel of an RGBA16F page, so the light can be put together at run time
/// from the sky and sun of the moment. The sun's coefficients are one set shared by every colour, and <paramref name="SunTint"/>
/// scales them per texel: its RGB is the tint divided by its largest channel, and <paramref name="Sun"/> holds that
/// channel's share.
/// </summary>
/// <param name="SkyRed">The sky's transfer for red, RGBA16F.</param>
/// <param name="SkyGreen">The sky's transfer for green, RGBA16F.</param>
/// <param name="SkyBlue">The sky's transfer for blue, RGBA16F.</param>
/// <param name="Sun">The sun's transfer shared by all three colours, RGBA16F.</param>
/// <param name="SunTint">The per texel RGB tint of the sun's transfer, RGBA8.</param>
public sealed record LightBakeTransfer(
    LightBakePage SkyRed, LightBakePage SkyGreen, LightBakePage SkyBlue, LightBakePage Sun, LightBakePage SunTint);

/// <summary>
/// A baked space or chunk on disk: the four hashes it was made from, where each draw sits on the lightmap pages, the
/// pages themselves and the shadow mask channels. A chunk also has its <see cref="Transfer"/>, and may have no plain light.
/// See "The .lightbake file" in docs/design.md.
/// </summary>
/// <param name="Irradiance">The irradiance pages, or null for a chunk with no Baked or Mixed light and no glowing surface. A space always has them.</param>
/// <param name="Direction">The direction pages, null exactly when <paramref name="Irradiance"/> is.</param>
public sealed record LightBakeFile(
    BakeInputs Inputs, BakeQuality Quality,
    IReadOnlyList<(ulong BakeId, LightmapRect Rect)> Draws,
    IReadOnlyList<LightBakePage>? Irradiance, IReadOnlyList<LightBakePage>? Direction, IReadOnlyList<LightBakePage> Mask,
    LightBakePage? ProbeIrradiance, LightBakePage? ProbeDepth, ProbeVolumeSettings? Probes,
    IReadOnlyList<(int LightKey, int Channel)> Channels, LightBakeReport Report)
{
    public const uint Magic = 0x4B41424C; // "LBAK"
    public const int Version = 2;

    /// <summary>The oldest version <see cref="Read"/> takes. Version 1 has no transfer and no chunk channels, and always has a plain light.</summary>
    public const int OldestVersion = 1;

    // magic 4, version 4, four hashes 32, quality 1.
    private const int HeaderSize = 41;

    private const byte HasProbeIrradiance = 1;
    private const byte HasProbeDepth = 2;
    private const byte HasProbes = 4;
    private const byte HasTransfer = 8;
    private const byte HasPlainLight = 16;

    /// <summary>Only for a chunk: its sky and sun transfer. Null for a space.</summary>
    public LightBakeTransfer? Transfer { get; init; }

    /// <summary>
    /// Only for a chunk: which mask channel each light has, keyed by the light's chunk-space hash
    /// (<see cref="BakeInputHash.Light"/> with the chunk's transform), which every copy of a chunk shares, unlike the
    /// game's light key. A space uses <see cref="Channels"/>.
    /// </summary>
    public IReadOnlyList<(ulong LightHash, int Channel)> ChunkChannels { get; init; } = [];

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

            if ((Irradiance is null) != (Direction is null))
            {
                throw new InvalidOperationException("A bake has both its irradiance and direction pages or neither.");
            }

            byte present = 0;
            present |= Irradiance is null ? (byte)0 : HasPlainLight;
            present |= ProbeIrradiance is null ? (byte)0 : HasProbeIrradiance;
            present |= ProbeDepth is null ? (byte)0 : HasProbeDepth;
            present |= Probes is null ? (byte)0 : HasProbes;
            present |= Transfer is null ? (byte)0 : HasTransfer;
            writer.Write(present);
            if (Irradiance is not null && Direction is not null)
            {
                WritePages(writer, Irradiance);
                WritePages(writer, Direction);
            }

            WritePages(writer, Mask);
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

            if (Transfer is { } transfer)
            {
                WritePage(writer, transfer.SkyRed);
                WritePage(writer, transfer.SkyGreen);
                WritePage(writer, transfer.SkyBlue);
                WritePage(writer, transfer.Sun);
                WritePage(writer, transfer.SunTint);
            }

            writer.Write(Channels.Count);
            foreach (var (lightKey, channel) in Channels)
            {
                writer.Write(lightKey);
                writer.Write(channel);
            }

            writer.Write(ChunkChannels.Count);
            foreach (var (lightHash, channel) in ChunkChannels)
            {
                writer.Write(lightHash);
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

    /// <summary>
    /// Reads a whole file. Throws <see cref="InvalidDataException"/> for a wrong magic or version, for cut-off bytes,
    /// and for a page larger than any bake makes: a lightmap page over <see cref="LightmapLayout.PageSize"/> a side, or
    /// a probe atlas over the size that holds <see cref="BakeFiles.MaxSpaceProbes"/> probes.
    /// </summary>
    public static LightBakeFile Read(ReadOnlySpan<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), writable: false);
        using var reader = new BinaryReader(stream);
        try
        {
            var (inputs, quality, version) = ReadHeader(reader);

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

            // Version 1 has the flags after the mask pages and always has a plain light. Version 2 has them first, as
            // whether the irradiance and direction pages follow depends on them.
            byte present = version >= 2 ? reader.ReadByte() : (byte)0;
            List<LightBakePage>? irradiance = null;
            List<LightBakePage>? direction = null;
            if (version < 2 || (present & HasPlainLight) != 0)
            {
                irradiance = ReadPages(reader, stream);
                direction = ReadPages(reader, stream);
            }

            var mask = ReadPages(reader, stream);
            if (version < 2)
            {
                present = reader.ReadByte();
            }

            var probeIrradiance = (present & HasProbeIrradiance) != 0
                ? ReadPage(reader, stream, BakedProbeAtlas.IrradianceAtlasSize, "probe irradiance atlas")
                : null;
            var probeDepth = (present & HasProbeDepth) != 0
                ? ReadPage(reader, stream, BakedProbeAtlas.DepthAtlasSize, "probe depth atlas")
                : null;
            ProbeVolumeSettings? probes = null;
            if ((present & HasProbes) != 0)
            {
                var min = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                var max = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                probes = new ProbeVolumeSettings(min, max, reader.ReadSingle());
            }

            LightBakeTransfer? transfer = null;
            if ((present & HasTransfer) != 0)
            {
                const string what = "transfer page";
                transfer = new LightBakeTransfer(
                    ReadPage(reader, stream, LightmapLayout.PageSize, what), ReadPage(reader, stream, LightmapLayout.PageSize, what),
                    ReadPage(reader, stream, LightmapLayout.PageSize, what), ReadPage(reader, stream, LightmapLayout.PageSize, what),
                    ReadPage(reader, stream, LightmapLayout.PageSize, what));
            }

            var channelCount = ReadCount(reader, stream, 8, "channels");
            var channels = new List<(int, int)>(channelCount);
            for (var i = 0; i < channelCount; i++)
            {
                channels.Add((reader.ReadInt32(), reader.ReadInt32()));
            }

            var chunkChannels = new List<(ulong, int)>();
            if (version >= 2)
            {
                var chunkChannelCount = ReadCount(reader, stream, 12, "chunk channels");
                chunkChannels.Capacity = chunkChannelCount;
                for (var i = 0; i < chunkChannelCount; i++)
                {
                    chunkChannels.Add((reader.ReadUInt64(), reader.ReadInt32()));
                }
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
                report)
            {
                Transfer = transfer,
                ChunkChannels = chunkChannels,
            };
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException(
                $"The .lightbake file is cut off: it ends at byte {stream.Length} before all its parts were read.");
        }
    }

    /// <summary>Reads only the header, so a freshness check never inflates a page. Throws like <see cref="Read"/> for a wrong magic or version.</summary>
    public static BakeInputs ReadInputs(ReadOnlySpan<byte> bytes) => ReadInputsAndQuality(bytes).Inputs;

    /// <summary>Reads only the header's inputs and quality. Throws like <see cref="ReadInputs"/>.</summary>
    internal static (BakeInputs Inputs, BakeQuality Quality) ReadInputsAndQuality(ReadOnlySpan<byte> bytes)
    {
        using var stream = new MemoryStream(bytes[..Math.Min(bytes.Length, HeaderSize)].ToArray(), writable: false);
        using var reader = new BinaryReader(stream);
        try
        {
            var header = ReadHeader(reader);
            return (header.Inputs, header.Quality);
        }
        catch (EndOfStreamException)
        {
            throw new InvalidDataException(
                $"The .lightbake header is cut off: the file is {stream.Length} bytes and the header takes {HeaderSize}.");
        }
    }

    private static (BakeInputs Inputs, BakeQuality Quality, int Version) ReadHeader(BinaryReader reader)
    {
        var magic = reader.ReadUInt32();
        if (magic != Magic)
        {
            throw new InvalidDataException(
                $"The .lightbake magic is wrong: found 0x{magic:X8}, expected 0x{Magic:X8}.");
        }

        var version = reader.ReadInt32();
        if (version < OldestVersion || version > Version)
        {
            throw new InvalidDataException(
                $"The .lightbake version is wrong: found {version}, but this reads versions {OldestVersion} and {Version}.");
        }

        var inputs = new BakeInputs(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64());
        var quality = reader.ReadByte();
        if (!Enum.IsDefined((BakeQuality)quality))
        {
            throw new InvalidDataException($"The .lightbake quality is wrong: found {quality}, expected 0 or 1.");
        }

        return (inputs, (BakeQuality)quality, version);
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
            pages.Add(ReadPage(reader, stream, LightmapLayout.PageSize, "lightmap page"));
        }

        return pages;
    }

    // largest is the most texels a side the page may have, which what names in the message.
    private static LightBakePage ReadPage(BinaryReader reader, MemoryStream stream, int largest, string what)
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

        var pageFormat = (LightBakePageFormat)format;
        if (width < 0 || height < 0)
        {
            throw new InvalidDataException(
                $"The .lightbake page is damaged: it is {width} by {height} texels, and a size can't be negative.");
        }

        // Checked before the pixels are allocated, so a damaged size can't ask for gigabytes.
        if (width > largest || height > largest)
        {
            throw new InvalidDataException(
                $"The .lightbake page is damaged: it is {width} by {height} texels, but a {what} is at most {largest} a side.");
        }

        var expected = ExpectedSize(width, height, pageFormat);
        if (rawLength != expected)
        {
            throw new InvalidDataException(
                $"The .lightbake page is damaged: a {width} by {height} {pageFormat} page takes {expected} bytes " +
                $"but the file says {rawLength}.");
        }

        var compressed = reader.ReadBytes(compressedLength);
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

        return new LightBakePage(width, height, pageFormat, pixels);
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
}
