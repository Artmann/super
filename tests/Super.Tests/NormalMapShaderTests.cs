using System.Text.RegularExpressions;

namespace Super.Tests;

/// <summary>
/// A cooked normal map is BC5, which keeps only red and green, so its blue samples as 0. A shader that turns a
/// texel into a normal with <c>texel.xyz * 2.0 - 1.0</c> gets normals pointing into the surface, which leaves it
/// dark. These check the shaders in the repo rebuild the third axis with <c>normalFrom</c> instead.
/// </summary>
public sealed partial class NormalMapShaderTests
{
    /// <summary>
    /// Shaders that decode xyz on purpose, with why. Terrain's normal layers are <c>GeneratedTexture</c>s, which stay
    /// RGBA8 with all four channels.
    /// </summary>
    private static readonly string[] ReadingGeneratedNormals = ["examples/Terrain/Shaders/terrain.frag"];

    [GeneratedRegex(@"\.(xyz|rgb)\s*\*\s*2\.0\s*-\s*1\.0")]
    private static partial Regex DecodesXyz();

    [Fact]
    public void No_shader_reads_a_cooked_normal_maps_blue()
    {
        string root = RepoRoot();
        var shaders = Directory.EnumerateFiles(Path.Combine(root, "src", "Rendition", "Shaders"))
            .Concat(Directory.EnumerateDirectories(Path.Combine(root, "examples"))
                .Select(example => Path.Combine(example, "Shaders"))
                .Where(Directory.Exists)
                .SelectMany(Directory.EnumerateFiles))
            .Where(path => path.EndsWith(".frag") || path.EndsWith(".glsl") || path.EndsWith(".vert"))
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/'))
            .ToList();
        Assert.Contains("examples/Archer/Shaders/clearing.frag", shaders);
        Assert.Contains("src/Rendition/Shaders/surface.glsl", shaders);

        string[] decoding = [.. shaders
            .Where(shader => !ReadingGeneratedNormals.Contains(shader))
            .Where(shader => DecodesXyz().IsMatch(File.ReadAllText(Path.Combine(root, shader))))];

        Assert.True(decoding.Length == 0,
            $"These decode a normal as .xyz * 2.0 - 1.0, but a cooked normal map's blue is 0; use normalFrom: {string.Join(", ", decoding)}");
    }

    private static string RepoRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "src", "Rendition", "Shaders", "surface.glsl"))
                && Directory.Exists(Path.Combine(folder.FullName, "examples")))
            {
                return folder.FullName;
            }
        }

        throw new DirectoryNotFoundException("No folder above the tests holds src/Rendition/Shaders and examples.");
    }
}
