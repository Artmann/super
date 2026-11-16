using System.Text.Json.Nodes;

namespace Rendition.Vulkan;

/// <summary>
/// Copies of Vulkan layer manifests that name their library by a full path. Homebrew's validation layer names its
/// library with no folder, which macOS only finds with DYLD_LIBRARY_PATH set, and a path relative to the manifest
/// stops working once the manifest is copied.
/// </summary>
internal static class LayerManifests
{
    /// <summary>
    /// Writes a copy of each manifest in <paramref name="from"/> to <paramref name="to"/>, and returns
    /// <paramref name="to"/>. A library named with no folder becomes a path into <paramref name="libraryFolder"/> where
    /// it's there, and a relative path becomes a full one.
    /// </summary>
    public static string WithFullPaths(string from, string libraryFolder, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string manifest in Directory.EnumerateFiles(from, "*.json"))
        {
            var root = JsonNode.Parse(File.ReadAllText(manifest));
            foreach (var layer in Layers(root))
            {
                if (layer?["library_path"]?.GetValue<string>() is { } library && FullPath(library, from, libraryFolder) is { } full)
                {
                    layer["library_path"] = full;
                }
            }

            // Games started at once write the same files, so each writes its own and moves it into place whole.
            string target = Path.Combine(to, Path.GetFileName(manifest));
            string written = $"{target}.{Environment.ProcessId}.tmp";
            File.WriteAllText(written, root?.ToJsonString());
            File.Move(written, target, overwrite: true);
        }

        return to;
    }

    private static string? FullPath(string library, string manifestFolder, string libraryFolder)
    {
        if (Path.IsPathRooted(library))
        {
            return null;
        }

        if (library.Contains('/'))
        {
            return Path.GetFullPath(Path.Combine(manifestFolder, library));
        }

        string inFolder = Path.Combine(libraryFolder, library);
        return File.Exists(inFolder) ? inFolder : null;
    }

    /// <summary>A manifest holds one layer, or several in a list.</summary>
    private static IEnumerable<JsonNode?> Layers(JsonNode? root) =>
        root?["layers"] is JsonArray all ? all : new[] { root?["layer"] };
}
