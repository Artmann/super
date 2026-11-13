using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Super;

// How Graphics.Save writes a JSON profile and Graphics.Load reads one, with a "Base" preset to start from.
internal static class GraphicsProfile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    /// <summary>
    /// Reads a profile. When it names a <c>"Base"</c>, <paramref name="preset"/> builds that quality, or Fastest for null,
    /// and the profile's settings are written over it.
    /// </summary>
    public static T Read<T>(string json, string source, Func<GraphicsQuality?, T> preset)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject profile && profile.Remove("Base", out var name))
            {
                json = OnTopOf(preset(Quality(name, source)), profile);
            }

            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new InvalidDataException($"{source} is empty. A graphics profile is a JSON object such as {{ \"Bloom\": false }}.");
        }
        catch (JsonException error)
        {
            throw new InvalidDataException($"{source} isn't a graphics profile: {error.Message}", error);
        }
        catch (ArgumentOutOfRangeException error)
        {
            throw new InvalidDataException($"{source} has a setting out of range: {error.Message}", error);
        }
    }

    // The quality a Base names, or null for Fastest.
    private static GraphicsQuality? Quality(JsonNode? name, string source)
    {
        string? text = name is JsonValue value && value.TryGetValue(out string? s) ? s : null;
        if (string.Equals(text, "Fastest", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // Enum.TryParse takes "2" as a number too, which isn't a preset's name.
        if (Enum.TryParse(text, ignoreCase: true, out GraphicsQuality quality) && Enum.IsDefined(quality) && !int.TryParse(text, out _))
        {
            return quality;
        }

        string given = name?.ToJsonString() ?? "null";
        throw new InvalidDataException($"{source} has a Base of {given}, but it can be Low, Medium, High, Ultra or Fastest.");
    }

    // The preset's settings with the profile's written over them, matching names exactly as reading does.
    private static string OnTopOf<T>(T preset, JsonObject profile)
    {
        var merged = JsonSerializer.SerializeToNode(preset, Options)!.AsObject();
        foreach (var (key, setting) in profile.ToList())
        {
            profile.Remove(key);
            merged[key] = setting;
        }

        return merged.ToJsonString();
    }
}
