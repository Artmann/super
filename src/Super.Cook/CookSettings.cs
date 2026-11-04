using System.Text.Json;
using System.Text.Json.Nodes;

namespace Super.Cook;

/// <summary>Works out how one source is cooked from the <c>cook.json</c> rules above it and its sidecar.</summary>
public static class CookSettings
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Merges the rules of every <c>cook.json</c> from the root down to the file's folder, then lays the sidecar over the top.
    /// A later rule beats an earlier one and a deeper file beats a shallower one. Settings merge per key.
    /// </summary>
    /// <param name="cookerIds">The rule keys allowed besides <c>match</c> and <c>cook</c>.</param>
    /// <returns>Whether to cook the file, and the settings under <paramref name="cookerId"/>.</returns>
    public static (bool Cook, JsonObject Settings) For(
        SourceFile file, string cookerId, IReadOnlySet<string> cookerIds, List<CookProblem> problems)
    {
        bool cook = true;
        var settings = new JsonObject();

        string[] parts = file.Name.Split('/');
        string folder = "";
        for (int depth = 0; depth < parts.Length; depth++)
        {
            string settingsName = folder + "cook.json";
            string relative = string.Join('/', parts.Skip(depth));
            var rules = ReadObject(file.Root, settingsName, problems)?["rules"];
            if (rules is JsonArray array)
            {
                foreach (var rule in array)
                {
                    ApplyRule(file.Root, settingsName, rule, relative, cookerId, cookerIds, problems, ref cook, settings);
                }
            }
            else if (rules is not null)
            {
                problems.Add(new CookProblem(Sources.Describe(file.Root, settingsName), null, "\"rules\" must be an array.", true));
            }

            if (depth < parts.Length - 1)
            {
                folder += parts[depth] + "/";
            }
        }

        string sidecarName = file.Name + ".cook.json";
        var sidecar = ReadObject(file.Root, sidecarName, problems);
        if (sidecar is not null)
        {
            Overlay(settings, sidecar);
        }

        return (cook, settings);
    }

    private static void ApplyRule(
        ISourceRoot root, string settingsName, JsonNode? rule, string relative, string cookerId,
        IReadOnlySet<string> cookerIds, List<CookProblem> problems, ref bool cook, JsonObject settings)
    {
        string where = Sources.Describe(root, settingsName);
        if (rule is not JsonObject obj || obj["match"] is not JsonValue matchValue || !matchValue.TryGetValue<string>(out var match))
        {
            problems.Add(new CookProblem(where, null, "A rule needs a \"match\" glob.", true));
            return;
        }

        if (!Glob.Matches(match, relative))
        {
            return;
        }

        foreach (var (key, value) in obj)
        {
            if (key == "match")
            {
                continue;
            }

            if (key == "cook")
            {
                if (value is JsonValue flag && flag.TryGetValue<bool>(out bool on))
                {
                    cook = on;
                }
                else if (value is not null)
                {
                    problems.Add(new CookProblem(where, null, $"\"cook\" in the rule \"{match}\" must be true or false.", true));
                }

                continue;
            }

            if (!cookerIds.Contains(key))
            {
                problems.Add(new CookProblem(where, null,
                    $"Unknown key \"{key}\" in the rule \"{match}\". Expected match, cook or one of: {string.Join(", ", cookerIds.Order(StringComparer.Ordinal))}.", true));
                continue;
            }

            if (key != cookerId)
            {
                continue;
            }

            if (value is JsonObject values)
            {
                Overlay(settings, values);
            }
            else
            {
                problems.Add(new CookProblem(where, null, $"\"{key}\" in the rule \"{match}\" must be an object.", true));
            }
        }
    }

    private static void Overlay(JsonObject target, JsonObject top)
    {
        foreach (var (key, value) in top)
        {
            target[key] = value?.DeepClone();
        }
    }

    /// <summary>Reads a settings file if it's there. A file that isn't an object is reported and treated as missing.</summary>
    private static JsonObject? ReadObject(ISourceRoot root, string name, List<CookProblem> problems)
    {
        byte[] bytes;
        string? path = root.FullPath(name);
        try
        {
            if (path is not null && !File.Exists(path))
            {
                return null;
            }

            bytes = root.Read(name);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

        string where = Sources.Describe(root, name);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            bytes = bytes[3..];
        }

        try
        {
            if (JsonNode.Parse(bytes, null, Lenient) is JsonObject obj)
            {
                return obj;
            }

            problems.Add(new CookProblem(where, null, "The settings must be a JSON object.", true));
        }
        catch (JsonException exception)
        {
            int? line = exception.LineNumber is { } n ? (int)n + 1 : null;
            problems.Add(new CookProblem(where, line, $"Can't read the settings: {exception.Message}", true));
        }

        return null;
    }
}
