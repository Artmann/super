using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Rendition;

/// <summary>
/// Where the compiled shaders in a folder came from and how to compile them again, read from the <c>.sources</c>
/// files that Shaders.targets leaves there: one for each project with shaders.
/// </summary>
internal sealed partial class ShaderSources
{
    private static readonly string[] StageExtensions = [".vert", ".frag", ".comp"];

    private readonly string _outputFolder;
    private readonly Source[] _sources;

    private ShaderSources(string outputFolder, Source[] sources)
    {
        _outputFolder = outputFolder;
        _sources = sources;
    }

    /// <summary>Every folder a saved file could be in: the stage files' and the includes'.</summary>
    public IEnumerable<string> Folders => _sources
        .SelectMany(source => new[] { source.Folder, source.IncludeFolder })
        .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when the folder names no sources, or they aren't there, as in a published game.</summary>
    public static ShaderSources? Read(string outputFolder)
    {
        if (!Directory.Exists(outputFolder))
        {
            return null;
        }

        var sources = new List<Source>();
        foreach (string file in Directory.EnumerateFiles(outputFolder, "*.sources").Order(StringComparer.OrdinalIgnoreCase))
        {
            string[] lines = File.ReadAllLines(file);
            if (lines.Length >= 4 && Directory.Exists(lines[3]))
            {
                sources.Add(new Source(lines[0], lines[1], lines[2], Path.GetFullPath(lines[3])));
            }
        }

        return sources.Count == 0 ? null : new ShaderSources(outputFolder, [.. sources]);
    }

    /// <summary>
    /// Every shader these saved files are part of: a stage file itself, and for any other file each stage file that
    /// includes it, directly or through another include.
    /// </summary>
    public List<Stage> StagesFor(IReadOnlyCollection<string> saved)
    {
        var savedPaths = new HashSet<string>(saved.Select(Path.GetFullPath), StringComparer.OrdinalIgnoreCase);
        var stages = new List<Stage>();
        foreach (var source in _sources)
        {
            if (!Directory.Exists(source.Folder))
            {
                continue;
            }

            foreach (string stage in Directory.EnumerateFiles(source.Folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!StageExtensions.Contains(Path.GetExtension(stage), StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (savedPaths.Contains(stage) || Includes(stage, source).Overlaps(savedPaths))
                {
                    stages.Add(new Stage(source, stage));
                }
            }
        }

        return stages;
    }

    /// <summary>
    /// Compiles one shader beside the compiled one and then swaps it in, so a failed compile leaves the old file.
    /// Returns what went wrong, or null.
    /// </summary>
    public string? Compile(Stage stage)
    {
        string target = Path.Combine(_outputFolder, stage.Name + ".spv");
        string scratch = target + ".new";
        try
        {
            using var process = Process.Start(Command(stage, scratch))!;
            var output = process.StandardOutput.ReadToEndAsync();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0)
            {
                File.Delete(scratch);
                return error + output.Result;
            }

            File.Move(scratch, target, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return $"Could not run {stage.Source.Glslc}: {e.Message}";
        }
    }

    private static ProcessStartInfo Command(Stage stage, string output)
    {
        var source = stage.Source;
        var start = new ProcessStartInfo(source.Glslc)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string flag in source.Flags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            start.ArgumentList.Add(flag);
        }

        start.ArgumentList.Add("-I");
        start.ArgumentList.Add(source.IncludeFolder);
        start.ArgumentList.Add(stage.Path);
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(output);
        return start;
    }

    /// <summary>Every file a shader includes, directly or through another include, as glslc would find them.</summary>
    private static HashSet<string> Includes(string stage, Source source)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var left = new Stack<string>();
        left.Push(stage);
        while (left.Count > 0)
        {
            string file = left.Pop();
            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (IOException)
            {
                // Being saved right now. Its next save event compiles it.
                continue;
            }

            foreach (Match match in IncludeLine().Matches(text))
            {
                string name = match.Groups[1].Value;
                string beside = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, name));
                string included = File.Exists(beside) ? beside : Path.GetFullPath(Path.Combine(source.IncludeFolder, name));
                if (File.Exists(included) && found.Add(included))
                {
                    left.Push(included);
                }
            }
        }

        return found;
    }

    [GeneratedRegex("""^\s*#\s*include\s+"([^"]+)"\s*$""", RegexOptions.Multiline)]
    private static partial Regex IncludeLine();

    /// <summary>One project's shaders: the compiler, its flags, where includes are found and where the stage files are.</summary>
    internal sealed record Source(string Glslc, string Flags, string IncludeFolder, string Folder);

    /// <summary>One stage file to compile, such as <c>lighting.frag</c>.</summary>
    internal sealed record Stage(Source Source, string Path)
    {
        /// <summary>The shader's name as <see cref="Renderer.ReloadShaders"/> wants it.</summary>
        public string Name => System.IO.Path.GetFileName(Path);
    }
}
