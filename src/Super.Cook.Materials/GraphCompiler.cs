using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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

    // What a game's own build gives every file (ImplicitUsings), less the namespaces for files, threads and the network.
    // Leaving them out doesn't stop a graph reaching them by full name: UsesOnlyPureApis does that. Nullable warnings are
    // never reported: only errors are.
    private const string ImplicitUsings = "global using System;\nglobal using System.Collections.Generic;\nglobal using System.Linq;\n";

    private static readonly Lazy<MetadataReference[]> References = new(LoadReferences);

    private static readonly Lock CacheLock = new();

    // The oldest compile is first, so it's the one dropped when the cache is full.
    private static readonly LinkedList<(string Hash, CompiledGraph Graph)> Cache = [];

    /// <summary>
    /// Compiles <paramref name="source"/>, named <paramref name="name"/> in diagnostics and stack frames, and finds its graph class.
    /// </summary>
    /// <param name="error">Takes each error and the line in the graph it is on, when it has one.</param>
    /// <returns>The graph, or null after reporting why there isn't one to <paramref name="error"/>.</returns>
    public static CompiledGraph? Compile(string name, byte[] source, Action<string, int?> error)
    {
        string hash = Convert.ToHexStringLower(SHA256.HashData(source));
        lock (CacheLock)
        {
            if (Find(hash) is { } cached)
            {
                return cached;
            }
        }

        var compiled = CompileNew(name, hash, source, error);
        if (compiled is null)
        {
            return null;
        }

        lock (CacheLock)
        {
            // Another thread may have compiled the same source meanwhile. Everyone gets the first one.
            if (Find(hash) is { } first)
            {
                return first;
            }

            Cache.AddLast((hash, compiled));
            while (Cache.Count > CacheLimit)
            {
                // Not unloaded here: another thread may still be running it. The context goes once nothing holds it.
                Cache.RemoveFirst();
            }
        }

        return compiled;
    }

    /// <summary>The cached graph for <paramref name="hash"/>, or null. Call it holding <see cref="CacheLock"/>.</summary>
    private static CompiledGraph? Find(string hash)
    {
        for (var node = Cache.First; node is not null; node = node.Next)
        {
            if (node.Value.Hash == hash)
            {
                return node.Value.Graph;
            }
        }

        return null;
    }

    private static CompiledGraph? CompileNew(string name, string hash, byte[] source, Action<string, int?> error)
    {
        var text = SourceText.From(source, source.Length);
        var tree = CSharpSyntaxTree.ParseText(text, CSharpParseOptions.Default, path: name);
        var usings = CSharpSyntaxTree.ParseText(SourceText.From(ImplicitUsings, System.Text.Encoding.UTF8), CSharpParseOptions.Default, path: "GlobalUsings.g.cs");
        var compilation = CSharpCompilation.Create(
            "graph-" + hash[..16],
            [tree, usings],
            References.Value,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                deterministic: true,
                nullableContextOptions: NullableContextOptions.Enable));

        using var image = new MemoryStream();
        var result = compilation.Emit(image, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.Embedded));
        if (!result.Success)
        {
            foreach (var diagnostic in result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error))
            {
                var span = diagnostic.Location.GetLineSpan();
                error(diagnostic.GetMessage(CultureInfo.InvariantCulture), span.IsValid ? span.StartLinePosition.Line + 1 : null);
            }

            return null;
        }

        // Only the graph's own file: the usings tree is fixed text.
        if (!UsesOnlyPureApis(name, tree, compilation.GetSemanticModel(tree), error))
        {
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
            error($"{name} must hold one class that derives from MaterialGraph. It has {has}.", null);
            return null;
        }

        return new CompiledGraph(graphs[0], context);
    }

    /// <summary>
    /// Reports every name in <paramref name="tree"/> that reaches files, the network, threads, the clock, the process or
    /// <c>System.Random</c>. CoreLib holds all of those, so leaving their usings out isn't enough.
    /// A <c>using</c> line for one of those namespaces is reported too, even when nothing uses it.
    /// </summary>
    /// <returns>Whether nothing was reported.</returns>
    private static bool UsesOnlyPureApis(string name, SyntaxTree tree, SemanticModel model, Action<string, int?> error)
    {
        var reported = new HashSet<(int Line, string Symbol)>();
        var pending = new Stack<SyntaxNode>();
        pending.Push(tree.GetRoot());
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is ExpressionSyntax && model.GetSymbolInfo(node).Symbol is { } symbol && Banned(symbol) is { } banned)
            {
                // The outermost name is enough: File.Exists is one mistake, not three.
                int line = node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                string shown = banned.ToDisplayString(Shown);
                if (reported.Add((line, shown)))
                {
                    error($"{name}: a graph can't use {shown}, since it must give the same pixels every time.", line);
                }

                continue;
            }

            // Pushed in reverse, so errors come out in the order they appear in the file.
            foreach (var child in node.ChildNodes().Reverse())
            {
                pending.Push(child);
            }
        }

        return reported.Count == 0;
    }

    private static readonly string[] BannedNamespaces =
    [
        "System.IO",
        "System.Net",
        "System.Threading",
        "System.Diagnostics",
        "System.Reflection",
        "System.Runtime.InteropServices",
    ];

    private static readonly string[] BannedTypes = ["System.Random", "System.Environment"];

    private static readonly string[] BannedMembers =
    [
        "System.DateTime.Now",
        "System.DateTime.UtcNow",
        "System.DateTime.Today",
        "System.DateTimeOffset.Now",
        "System.DateTimeOffset.UtcNow",
        "System.Guid.NewGuid",
    ];

    // System.IO.File.Exists, System.DateTime.Now, System.Random: namespaces and types, no parameters.
    private static readonly SymbolDisplayFormat Shown = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

    /// <summary>The symbol to name in the error when <paramref name="symbol"/> is banned, otherwise null.</summary>
    private static ISymbol? Banned(ISymbol symbol)
    {
        if (symbol is INamespaceSymbol space)
        {
            return InBannedNamespace(space.ToDisplayString()) ? space : null;
        }

        // The graph's own code is never banned, only what it calls.
        if (symbol.Locations.Any(location => location.IsInSource))
        {
            return null;
        }

        // new Random() binds to the constructor. Name the type instead.
        if (symbol is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
        {
            symbol = constructor.ContainingType;
        }

        // values.Sum() binds to Sum on IEnumerable<float>. Name the class that declares it instead.
        if (symbol is IMethodSymbol { ReducedFrom: { } extension })
        {
            symbol = extension;
        }

        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (type is not null && BannedTypes.Contains(type.OriginalDefinition.ToDisplayString(Shown), StringComparer.Ordinal))
        {
            return symbol;
        }

        if (symbol is IPropertySymbol or IMethodSymbol && BannedMembers.Contains(symbol.ToDisplayString(Shown), StringComparer.Ordinal))
        {
            return symbol;
        }

        return symbol.ContainingNamespace is { } container && InBannedNamespace(container.ToDisplayString()) ? symbol : null;
    }

    private static bool InBannedNamespace(string space)
    {
        return BannedNamespaces.Any(banned => space == banned || space.StartsWith(banned + ".", StringComparison.Ordinal));
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
