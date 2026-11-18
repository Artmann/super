using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using Super.Materials;

namespace Super.Cook.Materials;

/// <summary>
/// Compiles and runs a <c>*.material.cs</c> graph, or a <c>*.material.json</c> variant of one. The material cooker runs
/// the file it cooks, and the model cooker runs the graphs a model's materials name, so both report the same errors.
/// </summary>
public static class MaterialGraphs
{
    private const int FormatVersion = 1;

    /// <summary>The size a graph cooks at when nothing sets one, in pixels across.</summary>
    public const int DefaultSize = Constants.DefaultSize;

    /// <summary>Changes whenever <c>Super.Materials</c>, <c>Super.Random</c> or this library is rebuilt with a change, so every graph cooks again.</summary>
    public static int Version { get; } = FoldVersion();

    /// <summary>
    /// How many graphs run at once in the process, whichever sources and cooks they're for. Each holds its float images
    /// while it runs, so this, not the number of workers, sets how much memory graphs take. Each runs its nodes on every
    /// core already, so more than this gains little. Only running a graph waits for it, never waiting for a shared run.
    /// </summary>
    internal static readonly int RunsAtOnce = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);

    private static readonly SemaphoreSlim RunPermits = new(RunsAtOnce);

    /// <summary>What is wrong with <paramref name="size"/>, named <paramref name="key"/> in the message, or null when it is a size a graph can cook at.</summary>
    public static string? SizeProblem(string key, int size)
    {
        if (size < Constants.MinSize || size > Constants.MaxSize || !BitOperations.IsPow2(size))
        {
            return $"{key} must be a power of two from {Constants.MinSize} to {Constants.MaxSize}, not {size.ToString(CultureInfo.InvariantCulture)}.";
        }

        return null;
    }

    /// <summary>
    /// Runs the graph or variant at <paramref name="relativePath"/>, relative to the source's folder, at
    /// <paramref name="size"/> pixels across. Reading it through <see cref="CookInput.Read"/> tracks it, its graph and the
    /// images it reads, so a change to any of them cooks the source again. The images are relative to the file named.
    /// </summary>
    /// <param name="error">
    /// Takes each error, such as <c>bricks.material.cs line 7: scale must be at least 1, not 0.</c>. A line is in the
    /// graph's text, since the error is filed under the source, which is another file.
    /// </param>
    /// <returns>The images, or null after reporting why there are none.</returns>
    public static MaterialImages? Run(CookInput input, string relativePath, int size, Action<string> error)
    {
        return Run(input, relativePath.Replace('\\', '/'), size, error, null);
    }

    private static MaterialImages? Run(CookInput input, string relativePath, int size, Action<string> error, List<(string Path, byte[] Hash)>? reads)
    {
        if (!relativePath.EndsWith(".material.cs", StringComparison.OrdinalIgnoreCase)
            && !relativePath.EndsWith(".material.json", StringComparison.OrdinalIgnoreCase))
        {
            error($"{relativePath} isn't a material graph. A graph is a .material.cs or .material.json file.");
            return null;
        }

        byte[] bytes;
        try
        {
            bytes = input.Read(relativePath);
        }
        catch (FileNotFoundException exception)
        {
            error(exception.Message);
            return null;
        }

        return Run(input, relativePath, bytes, size, (message, _) => error(message), reads);
    }

    /// <summary>
    /// Runs the graph or variant at <paramref name="relativePath"/> as <see cref="Run(CookInput, string, int, Action{string})"/>
    /// does, and packs its images into RGBA8 pixels. With a cook cache, the pixels are kept in its step cache under the
    /// graph's code, the variant's values, the size and this library's version, with the hash of every image the graph
    /// read. A later cook with the same graph takes them from there after checking that each image is unchanged, so editing
    /// one of a model's graphs reruns only that graph. A graph can't read anything else, as the graph compiler checks.
    /// </summary>
    /// <remarks>
    /// A run is also shared in the process through <see cref="SharedGraphRuns"/>, under the same key, so another source
    /// that runs the same graph at the same size, such as a second model or the graph's own material, takes it from
    /// memory, and two that want it at once wait for one run.
    /// </remarks>
    /// <returns>The packed images, or null after reporting why there are none to <paramref name="error"/>.</returns>
    public static PackedImages? RunPacked(CookInput input, string relativePath, int size, Action<string> error)
    {
        relativePath = relativePath.Replace('\\', '/');
        string folder = relativePath[..(relativePath.LastIndexOf('/') + 1)];
        string? key;
        using (input.Time("recall graph runs"))
        {
            key = RunKey(input, relativePath, size);
            if (input.StepResults.HasCache && key is not null && input.StepResults.Recall(RunStep, key) is { } kept && KeptGraphRuns.Read(input, folder, kept) is { } packed)
            {
                return packed;
            }
        }

        var run = SharedGraphRuns.GetOrMake(key, input, folder, () =>
        {
            var reads = new List<(string Path, byte[] Hash)>();
            SharedGraphRuns.Run? made;
            using (input.Time("run graphs"))
            {
                made = Run(input, relativePath, size, error, reads) is { } images ? Share(images, reads) : null;
            }

            // Kept by the source that ran it. The others share the run, and the cache it was kept in, with it.
            Keep(input, key, made, reads);
            return made;
        });
        return run?.Packed;
    }

    /// <summary>
    /// Runs the source itself, a graph or a variant, or takes the run another source made of the same file at the same size. An error in the source's own graph has its line.
    /// </summary>
    internal static SharedGraphRuns.Run? RunSourceShared(CookInput input, int size, CookOutput output)
    {
        string? key = RunKey(input, null, size);
        return SharedGraphRuns.GetOrMake(key, input, "", () =>
        {
            var reads = new List<(string Path, byte[] Hash)>();
            SharedGraphRuns.Run? made;
            using (input.Time("run graphs"))
            {
                made = Run(input, null, input.Bytes, size, (message, line) => output.Error(message, line), reads) is { } images
                    ? Share(images, reads)
                    : null;
            }

            // Kept as a model's run is, so a model that waited for this one, or cooks later, finds it in the step cache.
            Keep(input, key, made, reads);
            return made;
        });
    }

    private static void Keep(CookInput input, string? key, SharedGraphRuns.Run? made, List<(string Path, byte[] Hash)> reads)
    {
        if (made is not null && key is not null && input.StepResults.HasCache)
        {
            using (input.Time("keep graph runs"))
            {
                input.StepResults.Keep(RunStep, key, KeptGraphRuns.Write(made.Packed, reads));
            }
        }
    }

    private static SharedGraphRuns.Run Share(MaterialImages images, List<(string Path, byte[] Hash)> reads)
    {
        var packed = new PackedImages(images.Size, images.Width, images.HeightDepth, MaterialPixels.Pack(images));
        return new SharedGraphRuns.Run(packed, [.. reads], MaterialChecks.Measure(images));
    }

    /// <summary>
    /// The key of a graph run: everything but the images the graph reads, or null when the graph can't be read, so the run
    /// reports why. <paramref name="relativePath"/> is null for the source itself.
    /// </summary>
    private static string? RunKey(CookInput input, string? relativePath, int size)
    {
        string fileName = relativePath ?? input.Name;
        if (!fileName.EndsWith(".material.cs", StringComparison.OrdinalIgnoreCase)
            && !fileName.EndsWith(".material.json", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            byte[] bytes = relativePath is null ? input.Bytes : input.Read(relativePath);
            byte[] graph = bytes;
            if (fileName.EndsWith(".material.json", StringComparison.OrdinalIgnoreCase))
            {
                if (Variant.Read(fileName, bytes, _ => { }) is not { } variant)
                {
                    return null;
                }

                string folder = relativePath is null ? "" : relativePath[..(relativePath.LastIndexOf('/') + 1)];
                graph = input.Read(folder + variant.Graph);
            }

            return string.Join('|',
                Version.ToString(CultureInfo.InvariantCulture),
                KeptGraphRuns.Format.ToString(CultureInfo.InvariantCulture),
                size.ToString(CultureInfo.InvariantCulture),
                relativePath is null ? input.Name : RootName(input.Name, relativePath),
                Convert.ToHexString(SHA256.HashData(bytes)),
                Convert.ToHexString(SHA256.HashData(graph)));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    private const string RunStep = "graph-run";

    /// <param name="path">The file's path from the source's folder, or null for the source itself.</param>
    private static MaterialImages? Run(
        CookInput input, string? path, byte[] bytes, int size, Action<string, int?> report, List<(string Path, byte[] Hash)>? reads = null)
    {
        var run = new GraphFileRun(input, path, report);
        if (run.GraphBytes(bytes) is not { } graph)
        {
            return null;
        }

        var compiled = GraphCompiler.Compile(run.GraphName, graph, run.GraphError);
        return compiled is null ? null : run.Execute(compiled, size, reads);
    }

    /// <summary>
    /// One run of a graph file, or of a variant and the graph it names: which file it is, where its graph is, and how its
    /// errors are told.
    /// </summary>
    private sealed class GraphFileRun(CookInput input, string? path, Action<string, int?> report)
    {
        private readonly string _fileName = path ?? input.Name;
        private readonly string _folder = path is null ? "" : path[..(path.LastIndexOf('/') + 1)];

        // The graph's path from the source's folder, or null when the source is the graph. Its errors name it, since a
        // line in the graph means nothing in the file the error is filed under.
        private string? _graphPath = path;
        private Variant? _variant;

        /// <summary>The graph's name in the source root, which its compiler's messages start with.</summary>
        public string GraphName => _graphPath is null ? input.Name : RootName(input.Name, _graphPath);

        /// <summary>
        /// The graph's code: <paramref name="bytes"/> for a graph, or the graph a variant names, read so a change to it
        /// cooks the source again. Null after reporting why there is none.
        /// </summary>
        public byte[]? GraphBytes(byte[] bytes)
        {
            if (!_fileName.EndsWith(".material.json", StringComparison.OrdinalIgnoreCase))
            {
                return bytes;
            }

            _variant = Variant.Read(_fileName, bytes, message => report(message, null));
            if (_variant is null)
            {
                return null;
            }

            // The graph's path is relative to the variant.
            _graphPath = _folder + _variant.Graph;
            try
            {
                return input.Read(_graphPath);
            }
            catch (FileNotFoundException exception)
            {
                report(exception.Message, null);
                return null;
            }
        }

        /// <summary>Reports an error in the graph, by its path and line when the graph isn't the source itself.</summary>
        public void GraphError(string message, int? line)
        {
            if (_graphPath is null)
            {
                report(message, line);
                return;
            }

            string own = GraphName + ": ";
            if (message.StartsWith(own, StringComparison.Ordinal))
            {
                message = message[own.Length..];
            }

            string where = line is { } number ? $"{_graphPath} line {number.ToString(CultureInfo.InvariantCulture)}" : _graphPath;
            report($"{where}: {message}", null);
        }

        /// <summary>Makes the graph, sets a variant's values on it and runs it, or reports why it failed.</summary>
        public MaterialImages? Execute(CompiledGraph compiled, int size, List<(string Path, byte[] Hash)>? reads)
        {
            try
            {
                var graph = (MaterialGraph)Activator.CreateInstance(compiled.Type)!;
                if (_variant is not null && !_variant.Apply(graph, _fileName, message => report(message, null)))
                {
                    return null;
                }

                SharedGraphRuns.CountRun();
                RunPermits.Wait();
                try
                {
                    return GraphRunner.Run(graph, size, new CookImages(input, _folder, reads));
                }
                finally
                {
                    RunPermits.Release();
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                var thrown = exception is TargetInvocationException { InnerException: { } inner } ? inner : exception;
                // A cook error is one line. Some exceptions add more, such as the actual value of an argument.
                string message = thrown.Message.Split('\n')[0].TrimEnd('\r');
                GraphError(message, LineIn(thrown, compiled.Type.Assembly));
                return null;
            }
        }
    }

    /// <summary>The name in the source root of <paramref name="relative"/>, a path from <paramref name="sourceName"/>'s folder.</summary>
    private static string RootName(string sourceName, string relative)
    {
        var parts = new List<string>(sourceName.Split('/')[..^1]);
        foreach (string part in relative.Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == ".." && parts.Count > 0)
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }

    /// <summary>The line of the first stack frame in the graph's own code, or null when the graph's code isn't on the stack.</summary>
    private static int? LineIn(Exception exception, Assembly graph)
    {
        foreach (var frame in new StackTrace(exception, fNeedFileInfo: true).GetFrames())
        {
            if (frame.GetMethod()?.Module.Assembly == graph && frame.GetFileLineNumber() is > 0 and var line)
            {
                return line;
            }
        }

        return null;
    }

    private static int FoldVersion()
    {
        byte[] ids = [
            .. CodeId.Of(typeof(MaterialGraph).Assembly).ToByteArray(),
            .. CodeId.Of(typeof(GameRandom).Assembly).ToByteArray(),
            .. CodeId.Of(typeof(MaterialGraphs).Assembly).ToByteArray(),
            .. BitConverter.GetBytes(FormatVersion),
        ];
        return BinaryPrimitives.ReadInt32LittleEndian(SHA256.HashData(ids));
    }
}
