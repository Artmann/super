namespace Super.Testing;

/// <summary>Content files held in memory, so a test can state its data next to its checks.</summary>
public sealed class MemoryContent : IContentFiles
{
    private readonly Dictionary<string, string> _files = [];
    private readonly HashSet<string> _models = [];

    /// <summary>Adds a file, or replaces it, by its path inside the content folder.</summary>
    public string this[string path]
    {
        set => _files[path] = value;
    }

    /// <summary>Once one model is added, only added models exist. Until then every model does.</summary>
    public void AddModel(string path) => _models.Add(path);

    public void Remove(string path) => _files.Remove(path);

    public IReadOnlyList<string>? List(string folder)
    {
        string prefix = folder + "/";
        string[] found = _files.Keys
            .Where(path => path.StartsWith(prefix, StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        return found.Length > 0 ? found : null;
    }

    public byte[] Read(string path) => System.Text.Encoding.UTF8.GetBytes(_files[path]);

    public bool HasModel(string path) => _models.Count == 0 || _models.Contains(path);
}
