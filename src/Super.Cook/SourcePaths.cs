namespace Super.Cook;

/// <summary>
/// Paths of the files a source names, relative to the source's folder, such as <c>../textures/ripples.png</c> beside
/// <c>models/a.glb</c>, turned into names in the source's root and back.
/// </summary>
internal static class SourcePaths
{
    /// <summary>The name in the root that <paramref name="relative"/> stands for beside <paramref name="sourceName"/>, or null when it leaves the root.</summary>
    public static string? ResolveName(string sourceName, string relative)
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
    public static string Normalise(string sourceName, string relative)
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

    /// <summary>The folder of a source, with its trailing slash, or empty for a source at the top of its root.</summary>
    public static string FolderOf(string sourceName)
    {
        int slash = sourceName.LastIndexOf('/');
        return slash < 0 ? "" : sourceName[..(slash + 1)];
    }
}
