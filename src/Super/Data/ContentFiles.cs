using Microsoft.Extensions.DependencyInjection;

namespace Super;

/// <summary>Where JSON content comes from. Games use the content folder, and tests can hold it in memory.</summary>
public interface IContentFiles
{
    /// <summary>
    /// Every .json file under a folder, as paths inside the content folder with forward
    /// slashes, in ordinal order. Null if the folder isn't there.
    /// </summary>
    IReadOnlyList<string>? List(string folder);

    byte[] Read(string path);

    /// <summary>Whether a cooked model exists. It answers true when there's no way to tell.</summary>
    bool HasModel(string path);
}

public static class ContentServices
{
    /// <summary>Reads JSON content from somewhere other than the content folder.</summary>
    public static IServiceCollection AddContentFiles(this IServiceCollection services, IContentFiles files) =>
        services.AddSingleton(files);
}

/// <summary>
/// The content folder on disk. A shipped game has it beside the program. A game run from
/// source finds it further up, in the project, and that's how live reload knows it may run.
/// </summary>
public sealed class ContentFolder : IContentFiles
{
    private const string FolderName = "content";
    private const string CookedFolder = "assets/cooked";

    private string? _root;
    private bool _searched;

    /// <summary>Finds the folder by walking up from the program.</summary>
    public ContentFolder()
    {
    }

    public ContentFolder(string root)
    {
        _root = root;
        _searched = true;
    }

    /// <summary>The folder's full path, if there is one.</summary>
    internal string? Root
    {
        get
        {
            if (!_searched)
            {
                _searched = true;
                _root = Upward().Select(folder => Path.Combine(folder, FolderName)).FirstOrDefault(Directory.Exists);
            }

            return _root;
        }
    }

    /// <summary>Whether this is a project's own folder rather than one shipped beside the program.</summary>
    internal bool IsSource
    {
        get
        {
            string beside = Path.Combine(AppContext.BaseDirectory, FolderName);
            return Root is { } root && !string.Equals(Path.GetFullPath(root), Path.GetFullPath(beside), StringComparison.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<string>? List(string folder)
    {
        if (Root is not { } root || !Directory.Exists(Path.Combine(root, folder)))
        {
            return null;
        }

        return Directory.GetFiles(Path.Combine(root, folder), "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    public byte[] Read(string path) => File.ReadAllBytes(Path.Combine(Root!, path));

    public bool HasModel(string path)
    {
        string[] cooked = Upward().Select(folder => Path.Combine(folder, CookedFolder)).Where(Directory.Exists).ToArray();
        return cooked.Length == 0 || cooked.Any(folder => File.Exists(Path.Combine(folder, path)));
    }

    /// <summary>The program's folder and each one above it.</summary>
    internal static IEnumerable<string> Upward()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            yield return folder.FullName;
        }
    }
}
