using System.Globalization;
using System.Numerics;
using System.Text;
using Rendition;
using StbImageSharp;

namespace Super.Cook;

/// <summary>
/// Turns an artist's colour table into a <see cref="ColorLutData"/>: a <c>.cube</c> file, as Resolve, Photoshop and most
/// grading tools export, or a <c>.lut.png</c> strip, the picture <c>--neutral-lut</c> stamps after a paint program has
/// graded it. See "A colour table" in docs/rendering.md.
/// </summary>
public sealed class LutCooker : ICooker
{
    public string Id => "lut";

    public int Version => 1;

    public IReadOnlyList<string> Extensions { get; } = [".cube", ".lut.png"];

    public void Cook(CookInput input, CookOutput output)
    {
        var lut = input.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? FromStrip(input.Bytes, input.Name, output)
            : FromCube(Encoding.UTF8.GetString(input.Bytes), input.Name, output);
        if (lut is not null)
        {
            output.Add(ContentKind.Data, lut.ToBytes());
        }
    }

    /// <summary>
    /// Reads a .cube file's 3D table. Its <c>LUT_3D_SIZE</c> sets the size, and its rows list the cells with red
    /// changing fastest. A 1D table, a domain other than 0 to 1, or a row that isn't three numbers is an error, with
    /// its line.
    /// </summary>
    internal static ColorLutData? FromCube(string text, string name, CookOutput output)
    {
        int size = 0;
        var cells = new List<Vector3>();
        string[] lines = text.Split('\n');
        for (int index = 0; index < lines.Length; index++)
        {
            int line = index + 1;
            string row = lines[index].Trim();
            if (row.Length == 0 || row[0] == '#')
            {
                continue;
            }

            string[] words = row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string? error = Keyword(words, row, ref size, out bool isKeyword);
            if (error is null && !isKeyword)
            {
                if (TryColor(words, out var color))
                {
                    cells.Add(color);
                }
                else
                {
                    error = $"has \"{row}\" where a cell's three numbers should be.";
                }
            }

            if (error is not null)
            {
                output.Error($"{name} {error}", line);
                return null;
            }
        }

        return Finished(size, cells, name, output);
    }

    // Reads a header line, such as LUT_3D_SIZE. It says whether the row was one, and gives the error's text after the
    // file's name when the row can't be read.
    private static string? Keyword(string[] words, string row, ref int size, out bool isKeyword)
    {
        isKeyword = true;
        switch (words[0].ToUpperInvariant())
        {
            case "TITLE":
                return null;
            case "LUT_1D_SIZE":
                return "is a 1D table. Only 3D tables, with LUT_3D_SIZE, can grade the picture.";
            case "LUT_3D_SIZE":
                return SizeError(words, out size);
            case "DOMAIN_MIN":
                return DomainError(words, row, 0f);
            case "DOMAIN_MAX":
                return DomainError(words, row, 1f);
            default:
                isKeyword = false;
                return null;
        }
    }

    private static string? SizeError(string[] words, out int size)
    {
        size = 0;
        bool sized = words.Length == 2
            && int.TryParse(words[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out size)
            && size >= ColorLutData.MinSize && size <= ColorLutData.MaxSize;
        return sized
            ? null
            : $"has LUT_3D_SIZE {string.Join(' ', words.Skip(1))}. It must be a whole number from {ColorLutData.MinSize} to {ColorLutData.MaxSize}.";
    }

    // Only the domain every table has by default, 0 to 1, is read.
    private static string? DomainError(string[] words, string row, float expected)
    {
        bool unit = TryColor(words.AsSpan(1), out var bound) && bound == new Vector3(expected);
        return unit ? null : $"has {row}. Only tables over 0 to 1 are read; export it without a custom domain.";
    }

    // The table, once the whole file is read, if its size line came and its cells fill it.
    private static ColorLutData? Finished(int size, List<Vector3> cells, string name, CookOutput output)
    {
        if (size == 0)
        {
            output.Error($"{name} has no LUT_3D_SIZE line, so its size is unknown.");
            return null;
        }

        if (cells.Count != size * size * size)
        {
            output.Error($"{name} has {cells.Count} cells, but LUT_3D_SIZE {size} needs {size * size * size}.");
            return null;
        }

        return Table(size, cells);
    }

    /// <summary>
    /// Reads a strip of <c>N</c> squares, each <c>N</c> pixels a side: blue rises from square to square, red to the
    /// right within each, and green downwards.
    /// </summary>
    internal static ColorLutData? FromStrip(byte[] bytes, string name, CookOutput output)
    {
        ImageResult image;
        try
        {
            image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlue);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            output.Error($"{name} is not an image it can read: {exception.Message}");
            return null;
        }

        int size = image.Height;
        if (image.Width != size * size || size < ColorLutData.MinSize || size > ColorLutData.MaxSize)
        {
            output.Error(
                $"{name} is {image.Width}x{image.Height}. A colour table strip is N squares of N pixels side by side, such as 1024x32, with N from {ColorLutData.MinSize} to {ColorLutData.MaxSize}.");
            return null;
        }

        var cells = new List<Vector3>(size * size * size);
        for (int blue = 0; blue < size; blue++)
        {
            for (int green = 0; green < size; green++)
            {
                for (int red = 0; red < size; red++)
                {
                    int pixel = ((green * image.Width) + (blue * size) + red) * 3;
                    cells.Add(new Vector3(image.Data[pixel], image.Data[pixel + 1], image.Data[pixel + 2]) / 255f);
                }
            }
        }

        return Table(size, cells);
    }

    // The cells, in the order both formats list them, with the unused fourth half of each set to 1.
    private static ColorLutData Table(int size, List<Vector3> cells)
    {
        var halves = new Half[cells.Count * 4];
        for (int i = 0; i < cells.Count; i++)
        {
            halves[(i * 4) + 0] = (Half)cells[i].X;
            halves[(i * 4) + 1] = (Half)cells[i].Y;
            halves[(i * 4) + 2] = (Half)cells[i].Z;
            halves[(i * 4) + 3] = (Half)1f;
        }

        return new ColorLutData { Size = size, Cells = halves };
    }

    private static bool TryColor(ReadOnlySpan<string> numbers, out Vector3 color)
    {
        color = default;
        if (numbers.Length != 3)
        {
            return false;
        }

        if (!float.TryParse(numbers[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float red)
            || !float.TryParse(numbers[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float green)
            || !float.TryParse(numbers[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float blue)
            || !float.IsFinite(red) || !float.IsFinite(green) || !float.IsFinite(blue))
        {
            return false;
        }

        color = new Vector3(red, green, blue);
        return true;
    }
}
