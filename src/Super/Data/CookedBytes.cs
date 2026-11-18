using System.Globalization;
using System.Text.Json;

namespace Super;

/// <summary>Reads cooked files whole, turning a failure into a <see cref="ContentException"/> that names the file.</summary>
internal static class CookedBytes
{
    /// <summary>The file's bytes. <paramref name="kind"/> says what it is in the message, such as "sound".</summary>
    public static byte[] Read(IContentFiles files, string path, string kind)
    {
        try
        {
            return files.Read(path);
        }
        catch (Exception problem) when (problem is IOException or UnauthorizedAccessException or KeyNotFoundException)
        {
            throw new ContentException([$"Could not read the {kind} {path}: {problem.Message}"]);
        }
    }

    /// <summary>
    /// The width in metres of a cooked material graph or variant, such as <c>materials/bricks.material.cs</c>, which
    /// shapes tile at. A name that isn't a cooked material, or a width that isn't a positive number, throws.
    /// </summary>
    public static float MaterialWidth(IContentFiles files, string name)
    {
        string part = name + "#material";
        if (files.KindOf(part) != ContentKind.Data)
        {
            if (files.KindOf(name) is null)
            {
                throw new ContentException([ContentNames.Missing(files, name)]);
            }

            throw new ContentException([$"{name} is not a cooked material. Materials are .material.cs and .material.json files."]);
        }

        float width;
        try
        {
            using var json = JsonDocument.Parse(Read(files, part, "material"));
            width = json.RootElement.GetProperty("width").GetSingle();
        }
        catch (Exception problem) when (problem is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new ContentException([$"{part} can't be read: {problem.Message}"]);
        }

        // Shapes divide by the width to tile, so zero, a negative or a non-number would spread NaN or flip the UVs.
        if (!float.IsFinite(width) || width <= 0f)
        {
            throw new ContentException([$"{part} has a width of {width.ToString(CultureInfo.InvariantCulture)} metres; it must be a positive number. Cook it again."]);
        }

        return width;
    }
}
