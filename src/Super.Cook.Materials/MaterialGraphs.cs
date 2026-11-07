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
        relativePath = relativePath.Replace('\\', '/');
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

        return Run(input, relativePath, bytes, size, (message, _) => error(message));
    }

    /// <summary>Runs the source itself, a graph or a variant. An error in the source's own graph has its line.</summary>
    internal static MaterialImages? RunSource(CookInput input, int size, CookOutput output)
    {
        return Run(input, null, input.Bytes, size, (message, line) => output.Error(message, line));
    }

    /// <param name="path">The file's path from the source's folder, or null for the source itself.</param>
    private static MaterialImages? Run(CookInput input, string? path, byte[] bytes, int size, Action<string, int?> report)
    {
        string fileName = path ?? input.Name;
        string folder = path is null ? "" : path[..(path.LastIndexOf('/') + 1)];

        // The graph's path from the source's folder, or null when the source is the graph. Its errors name it, since a
        // line in the graph means nothing in the file the error is filed under.
        string? graphPath = path;
        Variant? variant = null;
        if (fileName.EndsWith(".material.json", StringComparison.OrdinalIgnoreCase))
        {
            variant = Variant.Read(fileName, bytes, message => report(message, null));
            if (variant is null)
            {
                return null;
            }

            // The graph's path is relative to the variant. Reading it makes a change to the graph cook the source again.
            graphPath = folder + variant.Graph;
            try
            {
                bytes = input.Read(graphPath);
            }
            catch (FileNotFoundException exception)
            {
                report(exception.Message, null);
                return null;
            }
        }

        string graphName = graphPath is null ? input.Name : RootName(input.Name, graphPath);

        void GraphError(string message, int? line)
        {
            if (graphPath is null)
            {
                report(message, line);
                return;
            }

            string own = graphName + ": ";
            if (message.StartsWith(own, StringComparison.Ordinal))
            {
                message = message[own.Length..];
            }

            string where = line is { } number ? $"{graphPath} line {number.ToString(CultureInfo.InvariantCulture)}" : graphPath;
            report($"{where}: {message}", null);
        }

        var compiled = GraphCompiler.Compile(graphName, bytes, GraphError);
        if (compiled is null)
        {
            return null;
        }

        try
        {
            var graph = (MaterialGraph)Activator.CreateInstance(compiled.Type)!;
            if (variant is not null && !variant.Apply(graph, fileName, message => report(message, null)))
            {
                return null;
            }

            return GraphRunner.Run(graph, size, new CookImages(input, folder));
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
            .. typeof(MaterialGraph).Assembly.ManifestModule.ModuleVersionId.ToByteArray(),
            .. typeof(GameRandom).Assembly.ManifestModule.ModuleVersionId.ToByteArray(),
            .. typeof(MaterialGraphs).Assembly.ManifestModule.ModuleVersionId.ToByteArray(),
            .. BitConverter.GetBytes(FormatVersion),
        ];
        return BinaryPrimitives.ReadInt32LittleEndian(SHA256.HashData(ids));
    }
}
