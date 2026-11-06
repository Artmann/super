using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Super.Materials;

namespace Super.Cook.Materials;

/// <summary>A graph file compiled and loaded: its one graph class, and the context to unload it with.</summary>
internal sealed record CompiledGraph(Type Type, AssemblyLoadContext Context);

/// <summary>Compiles one <c>*.material.cs</c> file on its own with Roslyn, against <c>Super.Materials</c> and the runtime only.</summary>
internal static class GraphCompiler
{
    private const int CacheLimit = 32;

    private static readonly string[] FrameworkReferences =
    [
        "System.Private.CoreLib",
        "System.Runtime",
        "System.Numerics.Vectors",
        "System.Linq",
        "System.Collections",
    ];

    private static readonly Lazy<MetadataReference[]> References = new(LoadReferences);

    private static readonly Lock CacheLock = new();

    // The oldest compile is first, so it's the one dropped when the cache is full.
    private static readonly LinkedList<(string Hash, CompiledGraph Graph)> Cache = [];

    /// <summary>
    /// Compiles <paramref name="source"/>, named <paramref name="name"/> in diagnostics and stack frames, and finds its graph class.
    /// </summary>
    /// <returns>The graph, or null after reporting why there isn't one to <paramref name="output"/>.</returns>
    public static CompiledGraph? Compile(string name, byte[] source, CookOutput output)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(source));
        lock (CacheLock)
        {
            for (var node = Cache.First; node is not null; node = node.Next)
            {
                if (node.Value.Hash == hash)
                {
                    return node.Value.Graph;
                }
            }
        }

        var compiled = CompileNew(name, hash, source, output);
        if (compiled is null)
        {
            return null;
        }

        lock (CacheLock)
        {
            Cache.AddLast((hash, compiled));
            while (Cache.Count > CacheLimit)
            {
                Cache.First!.Value.Graph.Context.Unload();
                Cache.RemoveFirst();
            }
        }

        return compiled;
    }

    private static CompiledGraph? CompileNew(string name, string hash, byte[] source, CookOutput output)
    {
        var text = SourceText.From(source, source.Length);
        var tree = CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default, path: name);
        var compilation = CSharpCompilation.Create(
            "graph-" + hash[..16],
            [tree],
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release, deterministic: true));

        using var image = new MemoryStream();
        var result = compilation.Emit(image, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
        if (!result.Success)
        {
            foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                var span = diagnostic.Location.GetLineSpan();
                output.Error(diagnostic.GetMessage(CultureInfo.InvariantCulture), span.IsValid ? span.StartLinePosition.Line + 1 : null);
            }

            return null;
        }

        image.Position = 0;
        var context = new GraphLoadContext();
        var assembly = context.LoadFromStream(image);

        Type[] graphs = assembly.GetTypes()
            .Where(type => type.IsPublic && type.IsClass && !type.IsAbstract && type.IsSubclassOf(typeof(MaterialGraph)))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToArray();
        if (graphs.Length != 1)
        {
            context.Unload();
            string has = graphs.Length == 0 ? "none" : $"{graphs.Length}: {string.Join(", ", graphs.Select(type => type.Name))}";
            output.Error($"{name} must hold one class that derives from MaterialGraph. It has {has}.");
            return null;
        }

        return new CompiledGraph(graphs[0], context);
    }

    private static MetadataReference[] LoadReferences()
    {
        string listed = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("The runtime lists no trusted platform assemblies, so graphs can't be compiled.");
        var byName = listed
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .ToLookup(path => Path.GetFileNameWithoutExtension(path), StringComparer.OrdinalIgnoreCase);

        var references = new List<MetadataReference>();
        foreach (string name in FrameworkReferences)
        {
            string path = byName[name].FirstOrDefault()
                ?? throw new InvalidOperationException($"{name} isn't among the runtime's assemblies, so graphs can't be compiled.");
            references.Add(MetadataReference.CreateFromFile(path));
        }

        // The cooker's own copies, which are the ones the load context hands back.
        references.Add(MetadataReference.CreateFromFile(typeof(MaterialGraph).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(GameRandom).Assembly.Location));
        return [.. references];
    }

    /// <summary>Holds one compiled graph, and hands it the cooker's own <c>Super.Materials</c>, so <see cref="MaterialGraph"/> is the same type.</summary>
    private sealed class GraphLoadContext() : AssemblyLoadContext(isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name == typeof(MaterialGraph).Assembly.GetName().Name)
            {
                return typeof(MaterialGraph).Assembly;
            }

            if (assemblyName.Name == typeof(GameRandom).Assembly.GetName().Name)
            {
                return typeof(GameRandom).Assembly;
            }

            return null;
        }
    }
}
