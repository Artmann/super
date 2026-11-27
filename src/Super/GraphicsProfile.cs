using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Super;

// How Graphics.Save writes a JSON profile and Graphics.Load reads one, with "Extends" naming a preset to start from.
internal static class GraphicsProfile
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,

        // The groups of settings can't be replaced, only filled in, so a profile's group is read into the one there.
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
    };

    /// <summary>
    /// Reads a profile. When it has <c>"Extends"</c>, <paramref name="preset"/> builds the quality it names, or Fastest for null,
    /// and the profile's settings are written over it.
    /// </summary>
    public static T Read<T>(string json, string source, Func<GraphicsQuality?, T> preset)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject profile && profile.Remove("Extends", out var name))
            {
                json = OnTopOf(preset(Quality(name, source)), profile);
            }

            return JsonSerializer.Deserialize<T>(json, Options)
                ?? throw new InvalidDataException($"{source} is empty. A graphics profile is a JSON object such as {{ \"Picture\": {{ \"Bloom\": false }} }}.");
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

    // The names a --graphics profile file sets in its "Screen" group, matched exactly as reading does. A preset names none.
    public static IReadOnlySet<string> ScreenSettingsIn(string profile)
    {
        if (!profile.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            || JsonNode.Parse(File.ReadAllText(profile)) is not JsonObject root
            || root["Screen"] is not JsonObject screen)
        {
            return new HashSet<string>();
        }

        return screen.Select(setting => setting.Key).ToHashSet(StringComparer.Ordinal);
    }

    // The quality a profile extends, or null for Fastest.
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
        throw new InvalidDataException($"{source} extends {given}, but a profile can extend Low, Medium, High, Ultra or Fastest.");
    }

    // The preset's settings with the profile's written over them, matching names exactly as reading does.
    private static string OnTopOf<T>(T preset, JsonObject profile)
    {
        var merged = JsonSerializer.SerializeToNode(preset, Options)!.AsObject();
        WriteOver(merged, profile);
        return merged.ToJsonString();
    }

    // Writes each of the profile's settings over the preset's. A group in both is merged setting by setting, so a
    // profile that sets one setting of a group keeps the preset's others.
    private static void WriteOver(JsonObject preset, JsonObject profile)
    {
        foreach (var (key, setting) in profile.ToList())
        {
            profile.Remove(key);
            if (setting is JsonObject group && preset[key] is JsonObject presetGroup)
            {
                WriteOver(presetGroup, group);
            }
            else
            {
                preset[key] = setting;
            }
        }
    }
}
