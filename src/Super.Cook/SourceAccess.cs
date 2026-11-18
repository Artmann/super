namespace Super.Cook;

/// <summary>
/// Reads the files beside one source in its root, by exact name, and remembers each one a cooker used, so a change to
/// any of them counts as a change to the source. Safe to use from several threads.
/// </summary>
internal sealed class SourceAccess(SourceFile file)
{
    private readonly List<string> _inputs = [];
    private IReadOnlySet<string>? _names;

    /// <summary>Every file read, found or tracked, relative to the source's folder, in the order first used.</summary>
    public IReadOnlyList<string> Inputs
    {
        get
        {
            lock (_inputs)
            {
                return [.. _inputs];
            }
        }
    }

    /// <summary>Reads a file by a path relative to the source's folder, by its exact name, and tracks it.</summary>
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

        byte[] bytes = file.Root.Read(name);
        Track(relative);
        return bytes;
    }

    /// <summary>The nearest file called <paramref name="fileName"/> beside the source or under its folder, tracked, or null.</summary>
    public string? Find(string fileName)
    {
        string folder = SourcePaths.FolderOf(file.Name);
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

    /// <summary>The name in the root of a file by its path from the source's folder, or null when the root has none.</summary>
    public string? NameOf(string relative)
    {
        string? name = SourcePaths.ResolveName(file.Name, relative);
        return name is not null && Names().Contains(name) ? name : null;
    }

    /// <summary>Records a file the cooker used, by its path from the source's folder.</summary>
    public void Track(string relative)
    {
        string normal = SourcePaths.Normalise(file.Name, relative);
        lock (_inputs)
        {
            if (!_inputs.Contains(normal, StringComparer.Ordinal))
            {
                _inputs.Add(normal);
            }
        }
    }

    /// <summary>
    /// The root's files. The pipeline lists each root once per cook; another root is listed once per source the first time a
    /// cooker asks. Two threads may both list it, and either set is right.
    /// </summary>
    private IReadOnlySet<string> Names()
    {
        return _names ??= file.Root is ListedRoot listed
            ? listed.Names
            : new HashSet<string>(file.Root.Files(), StringComparer.Ordinal);
    }

    private string Resolve(string relative)
    {
        return SourcePaths.ResolveName(file.Name, relative)
            ?? throw new FileNotFoundException($"{relative} leaves the source root of {file.Name}.", relative);
    }
}
