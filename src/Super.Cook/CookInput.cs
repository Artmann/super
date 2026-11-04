using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Super.Cook;

/// <summary>What a cooker is given: one source, its settings, and a way to read the files beside it.</summary>
public sealed class CookInput
{
    private static readonly JsonSerializerOptions SettingsOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = true,
    };

    private readonly SourceFile _file;
    private readonly List<string> _inputs = [];

    internal CookInput(SourceFile file, byte[] bytes, JsonObject settings, string target)
    {
        _file = file;
        Bytes = bytes;
        Settings = settings;
        Target = target;
    }

    /// <summary>The source's name, such as <c>items/axe.json</c>.</summary>
    public string Name => _file.Name;

    public byte[] Bytes { get; }

    /// <summary>The path on disk, or null when the source isn't a file.</summary>
    public string? FullPath => _file.Root.FullPath(_file.Name);

    public JsonObject Settings { get; }

    public string Target { get; }

    /// <summary>Every file the cooker read through <see cref="Read"/>, <see cref="Find"/> or <see cref="Track"/>, relative to the source's folder.</summary>
    public IReadOnlyList<string> Inputs => _inputs;

    /// <summary>Reads the settings as <typeparamref name="T"/>. A setting that <typeparamref name="T"/> doesn't have is an error.</summary>
    public T SettingsAs<T>() where T : new()
    {
        return Settings.Deserialize<T>(SettingsOptions) ?? new T();
    }

    /// <summary>Reads a file by a path relative to the source's folder.</summary>
    public byte[] Read(string relative)
    {
        string name = Resolve(relative);
        Track(relative);
        return _file.Root.Read(name);
    }

    /// <summary>Looks for a file beside the source, then in every folder under it.</summary>
    /// <returns>The path relative to the source's folder, or null when there is none.</returns>
    public string? Find(string fileName)
    {
        string folder = Folder();
        string[] candidates = _file.Root.Files()
            .Where(name => name.StartsWith(folder, StringComparison.Ordinal)
                && name[folder.Length..].Split('/')[^1] == fileName)
            .Select(name => name[folder.Length..])
            .OrderBy(relative => relative.Count(c => c == '/'))
            .ThenBy(relative => relative, StringComparer.Ordinal)
            .ToArray();
        if (candidates.Length == 0)
        {
            return null;
        }

        Track(candidates[0]);
        return candidates[0];
    }

    /// <summary>Records a file the cooker opened some other way, so a change to it counts as a change to this source.</summary>
    public void Track(string relative)
    {
        if (!_inputs.Contains(relative, StringComparer.Ordinal))
        {
            _inputs.Add(relative);
        }
    }

    private string Folder()
    {
        int slash = _file.Name.LastIndexOf('/');
        return slash < 0 ? "" : _file.Name[..(slash + 1)];
    }

    private string Resolve(string relative)
    {
        var parts = new List<string>(Folder().Split('/', StringSplitOptions.RemoveEmptyEntries));
        foreach (string part in relative.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (parts.Count == 0)
                {
                    throw new FileNotFoundException($"{relative} leaves the source root of {_file.Name}.", relative);
                }

                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }
}
