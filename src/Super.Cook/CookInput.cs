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
    private IReadOnlySet<string>? _names;

    private readonly StepCache? _steps;

    internal CookInput(SourceFile file, byte[] bytes, JsonObject settings, string target, StepCache? steps = null)
    {
        _file = file;
        Bytes = bytes;
        Settings = settings;
        Target = target;
        _steps = steps;
    }

    /// <summary>
    /// What an earlier cook kept for <paramref name="key"/> of a slow step with <see cref="Keep"/>, or null when
    /// there's nothing, the cook has no cache, or the entry was damaged. The key must name everything the step's result
    /// depends on, its code's version included, so a change to any of them misses. Safe to call from several threads.
    /// </summary>
    /// <param name="step">The step's name, in letters, digits and dashes, such as <c>graph-run</c>.</param>
    public byte[]? Recall(string step, string key) => _steps?.Find(step, key);

    /// <summary>Keeps a slow step's result for <see cref="Recall"/> in later cooks. Does nothing when the cook has no cache.</summary>
    public void Keep(string step, string key, byte[] bytes) => _steps?.Keep(step, key, bytes);

    /// <summary>Whether <see cref="Recall"/> and <see cref="Keep"/> have a cache behind them.</summary>
    public bool HasStepCache => _steps is not null;

    /// <summary>The source's name, such as <c>items/axe.json</c>.</summary>
    public string Name => _file.Name;

    public byte[] Bytes { get; }

    /// <summary>The path on disk, or null when the source isn't a file.</summary>
    public string? FullPath => _file.Root.FullPath(_file.Name);

    public JsonObject Settings { get; }

    public string Target { get; }

    /// <summary>Every file the cooker read through <see cref="Read"/>, <see cref="Find"/> or <see cref="Track"/>, relative to the source's folder.</summary>
    public IReadOnlyList<string> Inputs => _inputs;

    /// <summary>Where this source's cook spent its time, phase by phase.</summary>
    internal PhaseClock Clock { get; } = new();

    /// <summary>
    /// Times a phase of the cook until the result is disposed, as in <c>using (input.Time("decode")) { ... }</c>.
    /// Time in a phase of the same name adds up. It shows in <c>super-cook --timings</c> and <c>--verbose</c>.
    /// </summary>
    public PhaseClock.Measure Time(string phase) => Clock.Start(phase);

    /// <summary>Reads the settings as <typeparamref name="T"/>. A setting that <typeparamref name="T"/> doesn't have is an error.</summary>
    public T SettingsAs<T>() where T : new()
    {
        return Settings.Deserialize<T>(SettingsOptions) ?? new T();
    }

    /// <summary>
    /// Reads a file by a path relative to the source's folder. The name must match the root's listing
    /// exactly, case included, on every OS, so a cook that works on Windows works everywhere.
    /// </summary>
    /// <exception cref="FileNotFoundException">The root has no file by that exact name. The message names one differing only in case.</exception>
    public byte[] Read(string relative)
    {
        string name = Resolve(relative);
        if (!Names().Contains(name))
        {
            string? other = Names()
                .Where(listed => string.Equals(listed, name, StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.Ordinal)
                .FirstOrDefault();
            throw new FileNotFoundException(other is null
                ? $"{name} isn't in the source root."
                : $"{name} isn't in the source root, but {other} is. Names are case-sensitive.", name);
        }

        byte[] bytes = _file.Root.Read(name);
        Track(relative);
        return bytes;
    }

    /// <summary>Looks for a file beside the source, then in every folder under it.</summary>
    /// <returns>The path relative to the source's folder, or null when there is none.</returns>
    public string? Find(string fileName)
    {
        string folder = Folder();
        string[] candidates = Names()
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

    /// <summary>
    /// The name in the root of a file by its path from the source's folder, such as <c>textures/ripples.png</c> for
    /// <c>../textures/ripples.png</c> beside <c>models/a.glb</c>, or null when the root has no such file. It isn't read
    /// or tracked, so for a file that's cooked as an asset of its own and only named by this one.
    /// </summary>
    public string? NameOf(string relative)
    {
        string? name = ResolveName(_file.Name, relative);
        return name is not null && Names().Contains(name) ? name : null;
    }

    /// <summary>
    /// Whether the root's Only picks <paramref name="name"/>, a name in the root such as <see cref="NameOf"/> gives, so it's
    /// cooked as an asset of its own. Without an Only every file is. A file Only leaves out can still be read, but the game
    /// can't load it by name.
    /// </summary>
    public bool IsPicked(string name) => Sources.Picks(_file.Root, name);

    /// <summary>Records a file the cooker opened some other way, so a change to it counts as a change to this source.</summary>
    public void Track(string relative)
    {
        string normal = Normalise(_file.Name, relative);
        if (!_inputs.Contains(normal, StringComparer.Ordinal))
        {
            _inputs.Add(normal);
        }
    }

    /// <summary>The root's files. The pipeline lists each root once per cook; another root is listed once per source the first time a cooker asks.</summary>
    private IReadOnlySet<string> Names()
    {
        return _names ??= _file.Root is ListedRoot listed
            ? listed.Names
            : new HashSet<string>(_file.Root.Files(), StringComparer.Ordinal);
    }

    private string Folder()
    {
        return FolderOf(_file.Name);
    }

    private static string FolderOf(string sourceName)
    {
        int slash = sourceName.LastIndexOf('/');
        return slash < 0 ? "" : sourceName[..(slash + 1)];
    }

    private string Resolve(string relative)
    {
        return ResolveName(_file.Name, relative)
            ?? throw new FileNotFoundException($"{relative} leaves the source root of {_file.Name}.", relative);
    }

    /// <summary>The name in the root that <paramref name="relative"/> stands for beside <paramref name="sourceName"/>, or null when it leaves the root.</summary>
    internal static string? ResolveName(string sourceName, string relative)
    {
        var parts = new List<string>(FolderOf(sourceName).Split('/', StringSplitOptions.RemoveEmptyEntries));
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
                    return null;
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

    /// <summary>The shortest path from the source's folder to <paramref name="relative"/>, with forward slashes.</summary>
    private static string Normalise(string sourceName, string relative)
    {
        string? name = ResolveName(sourceName, relative);
        if (name is null)
        {
            return relative.Replace('\\', '/');
        }

        string[] folder = FolderOf(sourceName).Split('/', StringSplitOptions.RemoveEmptyEntries);
        string[] target = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        int common = 0;
        while (common < folder.Length && common < target.Length - 1 && folder[common] == target[common])
        {
            common++;
        }

        var result = new List<string>();
        for (int i = common; i < folder.Length; i++)
        {
            result.Add("..");
        }

        result.AddRange(target[common..]);
        return string.Join('/', result);
    }
}
