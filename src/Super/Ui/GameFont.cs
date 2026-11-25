using System.Buffers.Binary;
using StbTrueTypeSharp;

namespace Super.Ui;

/// <summary>
/// A typeface for the interface, named by <see cref="ContentFonts.Font(string, string?)"/> and put on
/// text with <c>.Font(font)</c>. It's one TrueType or OpenType file, with another for its bold weight
/// if it has one; without one, bold text in it is drawn in the regular weight.
/// </summary>
public sealed class GameFont
{
    internal GameFont(string path, StbTrueType.stbtt_fontinfo regular, string? boldPath, StbTrueType.stbtt_fontinfo? bold)
    {
        Path = path;
        BoldPath = boldPath;
        Regular = regular;
        Bold = bold ?? regular;
    }

    /// <summary>The name of the regular weight's cooked file, such as <c>fonts/pirata-one.ttf</c>.</summary>
    public string Path { get; }

    /// <summary>The name of the bold weight's cooked file, or null when bold text is drawn in the regular weight.</summary>
    public string? BoldPath { get; }

    internal StbTrueType.stbtt_fontinfo Regular { get; }

    internal StbTrueType.stbtt_fontinfo Bold { get; }

    public override string ToString() => Path;

    /// <summary>
    /// Reads a font file's bytes, or stops with a <see cref="ContentException"/> naming the file. The
    /// cooker reads each font this way too, so a file that cooked always loads.
    /// </summary>
    internal static StbTrueType.stbtt_fontinfo Read(string path, byte[] bytes)
    {
        // The reader trusts the file's offsets and reads memory past its end on a cut-off one, so its
        // tables are checked to lie inside it first.
        if (TablesProblem(bytes) is { } problem)
        {
            throw new ContentException([$"{path} isn't a font this can read: {problem}. Use a TrueType (.ttf) or OpenType (.otf) file."]);
        }

        StbTrueType.stbtt_fontinfo? font;
        try
        {
            font = StbTrueType.CreateFont(bytes, 0);
        }
        catch (Exception thrown)
        {
            throw new ContentException([$"{path} isn't a font this can read: {thrown.Message} Use a TrueType (.ttf) or OpenType (.otf) file."]);
        }

        if (font is null)
        {
            throw new ContentException([$"{path} isn't a font this can read. Use a TrueType (.ttf) or OpenType (.otf) file."]);
        }

        if (StbTrueType.stbtt_FindGlyphIndex(font, 'A') == 0)
        {
            throw new ContentException([$"{path} has no letters: it has no glyph for A. Is it a symbol font?"]);
        }

        return font;
    }

    /// <summary>What's wrong with the file's table directory, or null when every table it needs lies inside it.</summary>
    private static string? TablesProblem(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12)
        {
            return "it's too short";
        }

        uint version = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (version is not (0x00010000u or 0x74727565u or 0x4F54544Fu))
        {
            // 1.0 and "true" are TrueType outlines and "OTTO" OpenType's; a collection, WOFF or anything else isn't read.
            return "it doesn't start like a single TrueType or OpenType font";
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(bytes[4..]);
        if (12 + count * 16 > bytes.Length)
        {
            return "it's cut off in its table directory";
        }

        var tags = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            var record = bytes.Slice(12 + i * 16, 16);
            string tag = System.Text.Encoding.ASCII.GetString(record[..4]);
            long offset = BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            long length = BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            if (offset + length > bytes.Length)
            {
                return $"its {tag.Trim()} table runs past the end of the file";
            }

            tags.Add(tag);
        }

        string[] needed = tags.Contains("CFF ") ? ["cmap", "head", "hhea", "hmtx", "maxp"] : ["cmap", "head", "hhea", "hmtx", "maxp", "loca", "glyf"];
        string? missing = needed.FirstOrDefault(tag => !tags.Contains(tag));
        return missing is null ? null : $"it has no {missing} table";
    }
}
