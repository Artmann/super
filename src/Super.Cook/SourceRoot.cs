using System.Text;

namespace Super.Cook;

/// <summary>A place source files live. A game can have several, and a name is the path from its root.</summary>
public interface ISourceRoot
{
    /// <summary>Names the root in errors.</summary>
    string Label { get; }

    /// <summary>Relative paths with <c>/</c>, leaving out dot-files and dot-folders.</summary>
    IEnumerable<string> Files();

    /// <summary>Throws <see cref="FileNotFoundException"/> when the file isn't there.</summary>
    byte[] Read(string relative);

    /// <summary>The path on disk, or null when the file isn't on disk.</summary>
    string? FullPath(string relative);
}

/// <summary>A folder on disk.</summary>
public sealed class FolderRoot(string folder) : ISourceRoot
{
    private readonly string _folder = Path.GetFullPath(folder);

    public string Label => _folder;

    public IEnumerable<string> Files()
    {
        var found = new List<string>();
        Walk(_folder, "", found);
        return found;
    }

    public byte[] Read(string relative)
    {
        return File.ReadAllBytes(Path.Combine(_folder, relative));
    }

    /// <summary>With the separator of the OS, so an error line names the file the way the OS and editors write it.</summary>
    public string? FullPath(string relative)
    {
        return Path.Combine(_folder, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static void Walk(string folder, string prefix, List<string> found)
    {
        foreach (string file in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetFileName(file);
            if (!name.StartsWith('.'))
            {
                found.Add(prefix + name);
            }
        }

        foreach (string sub in Directory.EnumerateDirectories(folder))
        {
            string name = Path.GetFileName(sub);
            if (!name.StartsWith('.'))
            {
                Walk(sub, prefix + name + "/", found);
            }
        }
    }
}

/// <summary>Files held in memory, for tests and tools that make sources up.</summary>
public sealed class MemoryRoot : ISourceRoot
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public string Label => "memory";

    public string this[string name]
    {
        set => _files[name] = Encoding.UTF8.GetBytes(value);
    }

    public void Add(string name, byte[] bytes)
    {
        _files[name] = bytes;
    }

    public void Remove(string name)
    {
        _files.Remove(name);
    }

    public IEnumerable<string> Files()
    {
        return _files.Keys
            .Where(name => !name.Split('/').Any(part => part.StartsWith('.')))
            .ToList();
    }

    public byte[] Read(string relative)
    {
        return _files.TryGetValue(relative, out var bytes)
            ? bytes
            : throw new FileNotFoundException($"memory:{relative} isn't there.", relative);
    }

    public string? FullPath(string relative)
    {
        return null;
    }
}

/// <summary>
/// A root listed once, for one cook. Listing a big folder takes time, and the settings, the gathering and every cooker that
/// looks for a file beside its source would otherwise each list it again. Changes during the cook are seen by the next one.
/// </summary>
internal sealed class ListedRoot(ISourceRoot inner) : ISourceRoot
{
    private readonly List<string> _files = inner.Files().ToList();
    private HashSet<string>? _names;

    public string Label => inner.Label;

    /// <summary>The listing as a set, made once and shared by every source of the cook.</summary>
    public IReadOnlySet<string> Names
    {
        get
        {
            // Two threads may both make it. Either set is right, so it doesn't matter which one stays.
            return _names ??= new HashSet<string>(_files, StringComparer.Ordinal);
        }
    }

    public IEnumerable<string> Files() => _files;

    public byte[] Read(string relative) => inner.Read(relative);

    public string? FullPath(string relative) => inner.FullPath(relative);
}

/// <summary>One source: the root it came from and its name.</summary>
public sealed record SourceFile(ISourceRoot Root, string Name)
{
    /// <summary>The full path when there is one, otherwise <c>label:name</c>.</summary>
    public string Describe()
    {
        return Sources.Describe(Root, Name);
    }
}

public static class Sources
{
    /// <summary>Lists every asset across the roots, sorted by name. Settings files, buffers and notes are left out.</summary>
    public static (IReadOnlyList<SourceFile> Files, IReadOnlyList<CookProblem> Problems) Gather(IReadOnlyList<ISourceRoot> roots)
    {
        var problems = new List<CookProblem>();
        var byName = new Dictionary<string, SourceFile>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var names = root.Files().ToList();
            var all = new HashSet<string>(names, StringComparer.Ordinal);
            foreach (string name in names)
            {
                if (IsSidecar(name))
                {
                    string target = name[..^".cook.json".Length];
                    if (!all.Contains(target))
                    {
                        // A warning, so a clone without sources that stay out of git, such as licensed models, still builds.
                        problems.Add(new CookProblem(Describe(root, name), null,
                            $"No source file {target}; skipped.", false));
                    }

                    continue;
                }

                if (!IsAsset(name))
                {
                    continue;
                }

                if (byName.TryGetValue(name, out var first))
                {
                    problems.Add(new CookProblem(Describe(root, name), null,
                        $"{name} is in two roots, {first.Describe()} and {Describe(root, name)}. A name must come from one place.", true));
                    continue;
                }

                byName[name] = new SourceFile(root, name);
            }
        }

        var files = byName.Values.OrderBy(file => file.Name, StringComparer.Ordinal).ToList();
        return (files, problems);
    }

    internal static string Describe(ISourceRoot root, string name)
    {
        return root.FullPath(name) ?? $"{root.Label}:{name}";
    }

    internal static bool IsSidecar(string name)
    {
        return name.EndsWith(".cook.json", StringComparison.Ordinal) && name != ".cook.json" && !name.EndsWith("/.cook.json", StringComparison.Ordinal);
    }

    private static bool IsAsset(string name)
    {
        if (name == "cook.json" || name.EndsWith("/cook.json", StringComparison.Ordinal))
        {
            return false;
        }

        string extension = Path.GetExtension(name);
        return !extension.Equals(".bin", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".md", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase);
    }
}
