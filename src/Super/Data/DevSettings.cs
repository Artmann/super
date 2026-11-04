using System.Text.Json;

namespace Super;

/// <summary>
/// <c>content.dev.json</c>, which the build writes beside the cooked content. A game that finds it is
/// running from source, and knows where the sources are and which tool cooks them. It's never published.
/// </summary>
/// <param name="Roots">The full path of every content root.</param>
/// <param name="Tool">The full path of <c>super-cook.dll</c>, run with <c>dotnet</c>.</param>
/// <param name="Target">The platform the content was cooked for, such as <c>desktop</c>.</param>
public sealed record DevSettings(string[] Roots, string Tool, string Target)
{
    public const string FileName = "content.dev.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NewLine = "\n",
    };

    /// <summary>Reads the file, or returns null when there isn't one.</summary>
    /// <exception cref="ContentException">The file is there but isn't valid.</exception>
    public static DevSettings? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        DevSettings? settings;
        try
        {
            settings = JsonSerializer.Deserialize<DevSettings>(File.ReadAllBytes(path), Options);
        }
        catch (JsonException exception)
        {
            throw new ContentException([$"{path}: {exception.Message}"]);
        }

        if (settings is null || settings.Roots is null || string.IsNullOrEmpty(settings.Tool) || string.IsNullOrEmpty(settings.Target))
        {
            throw new ContentException([$"{path}: needs \"roots\", \"tool\" and \"target\". Building the game writes it again."]);
        }

        return settings;
    }

    public void Write(string path)
    {
        string? folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
        }

        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, Options));
    }
}
