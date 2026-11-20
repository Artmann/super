using System.Numerics;
using System.Runtime.InteropServices;

namespace Rendition;

/// <summary>
/// An artist's colour table, cooked from a .cube file or a PNG strip: a cube of <see cref="Size"/> cells a side, each the
/// colour the cell's own sRGB-encoded colour turns into. Red runs across, green down and blue deep, as .cube files
/// list them. Upload it with <see cref="Renderer.CreateColorLut"/>. See "A colour table" in docs/rendering.md.
/// </summary>
public sealed class ColorLutData
{
    /// <summary>The fewest cells a side a table may have.</summary>
    public const int MinSize = 2;

    /// <summary>The most cells a side a table may have.</summary>
    public const int MaxSize = 128;

    private const uint Magic = 0x5455_4C52; // "RLUT"
    private const int Version = 1;

    /// <summary>How many cells each side has, from <see cref="MinSize"/> to <see cref="MaxSize"/>.</summary>
    public required int Size { get; init; }

    /// <summary>Four halves a cell, red, green, blue and an unused 1, with red changing fastest, then green, then blue.</summary>
    public required Half[] Cells { get; init; }

    /// <summary>A table that changes nothing, <paramref name="size"/> cells a side.</summary>
    public static ColorLutData Neutral(int size) => From(size, color => color);

    /// <summary>A table <paramref name="size"/> cells a side whose every cell is <paramref name="grade"/> of the colour it stands for.</summary>
    public static ColorLutData From(int size, Func<Vector3, Vector3> grade)
    {
        ArgumentNullException.ThrowIfNull(grade);
        CheckSize(size);
        var cells = new Half[size * size * size * 4];
        float last = size - 1;
        int index = 0;
        for (int blue = 0; blue < size; blue++)
        {
            for (int green = 0; green < size; green++)
            {
                for (int red = 0; red < size; red++)
                {
                    var color = grade(new Vector3(red / last, green / last, blue / last));
                    cells[index++] = (Half)color.X;
                    cells[index++] = (Half)color.Y;
                    cells[index++] = (Half)color.Z;
                    cells[index++] = (Half)1f;
                }
            }
        }

        return new ColorLutData { Size = size, Cells = cells };
    }

    /// <summary>Throws unless the table has a size it may have and a cell for each of its places.</summary>
    public void Check()
    {
        CheckSize(Size);
        ArgumentNullException.ThrowIfNull(Cells);
        int expected = Size * Size * Size * 4;
        if (Cells.Length != expected)
        {
            throw new ArgumentException($"A colour table {Size} cells a side takes {expected} halves, not {Cells.Length}.");
        }
    }

    /// <summary>The bytes of the table, as <see cref="Read"/> reads them.</summary>
    public byte[] ToBytes()
    {
        Check();
        var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(Size);
            writer.Write(MemoryMarshal.AsBytes(Cells.AsSpan()));
        }

        return stream.ToArray();
    }

    /// <summary>Reads a table written by <see cref="ToBytes"/>. <paramref name="name"/> is only for messages.</summary>
    public static ColorLutData Read(Stream stream, string name)
    {
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (reader.ReadUInt32() != Magic || reader.ReadInt32() != Version)
        {
            throw new InvalidDataException($"{name} is not a current colour table. Cook it again.");
        }

        int size = reader.ReadInt32();
        if (size < MinSize || size > MaxSize)
        {
            throw new InvalidDataException($"{name} is {size} cells a side, outside {MinSize} to {MaxSize}. Cook it again.");
        }

        var cells = new Half[size * size * size * 4];
        reader.BaseStream.ReadExactly(MemoryMarshal.AsBytes(cells.AsSpan()));
        return new ColorLutData { Size = size, Cells = cells };
    }

    private static void CheckSize(int size)
    {
        if (size < MinSize || size > MaxSize)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, $"A colour table has {MinSize} to {MaxSize} cells a side.");
        }
    }
}
