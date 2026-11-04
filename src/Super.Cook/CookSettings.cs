using System.Text.Json;
using System.Text.Json.Nodes;

namespace Super.Cook;

/// <summary>
/// Works out how each source is cooked from the <c>cook.json</c> rules above it and its sidecar. Build one per cook run:
/// every <c>cook.json</c> and sidecar in the roots is read and checked once, in the constructor, and its problems land in
/// <see cref="Problems"/> once, whether or not any rule matches a file.
/// </summary>
public sealed class CookSettings
{
    private static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record Rule(string Match, bool? Cook, IReadOnlyDictionary<string, JsonObject> Cookers);

    private readonly Dictionary<(ISourceRoot Root, string Name), IReadOnlyList<Rule>> _rules = new();
    private readonly Dictionary<(ISourceRoot Root, string Name), JsonObject> _sidecars = new();
    private readonly List<CookProblem> _problems = [];
    private readonly IReadOnlySet<string> _cookerIds;

    /// <param name="roots">Every root being cooked. A <c>cook.json</c> in one root never touches another.</param>
    /// <param name="cookerIds">The rule keys allowed besides <c>match</c> and <c>cook</c>.</param>
    public CookSettings(IReadOnlyList<ISourceRoot> roots, IReadOnlySet<string> cookerIds)
    {
        _cookerIds = cookerIds;
        foreach (var root in roots)
        {
            foreach (string name in root.Files().Order(StringComparer.Ordinal))
            {
                if (name == "cook.json" || name.EndsWith("/cook.json", StringComparison.Ordinal))
                {
                    LoadRules(root, name);
                }
                else if (Sources.IsSidecar(name))
                {
                    LoadSidecar(root, name);
                }
            }
        }
    }

    /// <summary>Mistakes in the settings files, each reported once.</summary>
    public IReadOnlyList<CookProblem> Problems => _problems;

    /// <summary>
    /// Merges the rules of every <c>cook.json</c> from the root down to the file's folder, then lays the sidecar over the top.
    /// A later rule beats an earlier one and a deeper file beats a shallower one. Settings merge per key.
    /// </summary>
    /// <returns>Whether to cook the file, and the settings under <paramref name="cookerId"/>.</returns>
    public (bool Cook, JsonObject Settings) For(SourceFile file, string cookerId)
    {
        bool cook = true;
        var settings = new JsonObject();

        string[] parts = file.Name.Split('/');
        string folder = "";
        for (int depth = 0; depth < parts.Length; depth++)
        {
            if (_rules.TryGetValue((file.Root, folder + "cook.json"), out var rules))
            {
                string relative = string.Join('/', parts.Skip(depth));
                foreach (var rule in rules)
                {
                    if (!Glob.Matches(rule.Match, relative))
                    {
                        continue;
                    }

                    if (rule.Cook is { } on)
                    {
                        cook = on;
                    }

                    if (rule.Cookers.TryGetValue(cookerId, out var values))
                    {
                        Overlay(settings, values);
                    }
                }
            }

            if (depth < parts.Length - 1)
            {
                folder += parts[depth] + "/";
            }
        }

        if (_sidecars.TryGetValue((file.Root, file.Name + ".cook.json"), out var sidecar))
        {
            Overlay(settings, sidecar);
        }

        return (cook, settings);
    }

    private void LoadRules(ISourceRoot root, string name)
    {
        string where = Sources.Describe(root, name);
        if (ReadObject(root, name) is not { } obj)
        {
            return;
        }

        var rules = new List<Rule>();
        _rules[(root, name)] = rules;
        if (obj["rules"] is null)
        {
            return;
        }

        if (obj["rules"] is not JsonArray array)
        {
            Report(where, null, "\"rules\" must be an array.");
            return;
        }

        foreach (var node in array)
        {
            if (node is not JsonObject rule || rule["match"] is not JsonValue matchValue || !matchValue.TryGetValue<string>(out var match))
            {
                Report(where, null, "A rule needs a \"match\" glob.");
                continue;
            }

            bool? cook = null;
            var cookers = new Dictionary<string, JsonObject>();
            foreach (var (key, value) in rule)
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
                    else
                    {
                        Report(where, null, $"\"cook\" in the rule \"{match}\" must be true or false.");
                    }
                }
                else if (!_cookerIds.Contains(key))
                {
                    Report(where, null,
                        $"Unknown key \"{key}\" in the rule \"{match}\". Expected match, cook or one of: {string.Join(", ", _cookerIds.Order(StringComparer.Ordinal))}.");
                }
                else if (value is JsonObject values)
                {
                    cookers[key] = values;
                }
                else
                {
                    Report(where, null, $"\"{key}\" in the rule \"{match}\" must be an object.");
                }
            }

            rules.Add(new Rule(match, cook, cookers));
        }
    }

    private void LoadSidecar(ISourceRoot root, string name)
    {
        if (ReadObject(root, name) is { } obj)
        {
            _sidecars[(root, name)] = obj;
        }
    }

    private void Report(string file, int? line, string message)
    {
        _problems.Add(new CookProblem(file, line, message, true));
    }

    private static void Overlay(JsonObject target, JsonObject top)
    {
        foreach (var (key, value) in top)
        {
            target[key] = value?.DeepClone();
        }
    }

    /// <summary>Reads a settings file. One that isn't an object is reported and treated as missing.</summary>
    private JsonObject? ReadObject(ISourceRoot root, string name)
    {
        string where = Sources.Describe(root, name);
        byte[] bytes;
        try
        {
            bytes = root.Read(name);
        }
        catch (FileNotFoundException)
        {
            return null;
        }

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

            Report(where, null, "The settings must be a JSON object.");
        }
        catch (JsonException exception)
        {
            int? line = exception.LineNumber is { } n ? (int)n + 1 : null;
            Report(where, line, $"Can't read the settings: {exception.Message}");
        }

        return null;
    }
}
