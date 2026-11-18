using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Super.Cook.Materials;

/// <summary>
/// Checks that a graph's code reaches nothing that could give different pixels on another run: files, the network,
/// threads, the clock, the process or <c>System.Random</c>.
/// </summary>
internal static class PureApiCheck
{
    private static readonly string[] BannedNamespaces =
    [
        "System.IO",
        "System.Net",
        "System.Threading",
        "System.Diagnostics",
        "System.Reflection",
        "System.Runtime.InteropServices",
    ];

    // The nullable attributes change no pixels, so they stay allowed.
    private static readonly string[] AllowedNamespaces = ["System.Diagnostics.CodeAnalysis"];

    // TimeProvider reads the clock, HashCode is seeded per process, and Activator builds any type by name.
    private static readonly string[] BannedTypes =
    [
        "System.Random",
        "System.Environment",
        "System.TimeProvider",
        "System.HashCode",
        "System.Activator",
    ];

    private static readonly string[] BannedMembers =
    [
        "System.DateTime.Now",
        "System.DateTime.UtcNow",
        "System.DateTime.Today",
        "System.DateTimeOffset.Now",
        "System.DateTimeOffset.UtcNow",
        "System.Guid.NewGuid",
        "System.Guid.CreateVersion7",
        // A string's hash code changes every time the process starts.
        "System.String.GetHashCode",
        "System.Object.GetHashCode",
        // Both reach any type or method by its name, such as System.IO.File.Exists.
        "System.Type.GetType",
        "System.Delegate.CreateDelegate",
    ];

    // System.IO.File.Exists, System.DateTime.Now, System.Random: namespaces and types, no parameters.
    private static readonly SymbolDisplayFormat Shown = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None,
        memberOptions: SymbolDisplayMemberOptions.IncludeContainingType);

    /// <summary>
    /// Reports every name in <paramref name="tree"/> that reaches files, the network, threads, the clock, the process or
    /// <c>System.Random</c>. CoreLib holds all of those, so leaving their usings out isn't enough.
    /// A <c>using</c> line for one of those namespaces is reported too, even when nothing uses it.
    /// </summary>
    /// <returns>Whether nothing was reported.</returns>
    public static bool UsesOnlyPureApis(string name, SyntaxTree tree, SemanticModel model, Action<string, int?> error)
    {
        var reported = new HashSet<(int Line, string Symbol)>();
        var pending = new Stack<SyntaxNode>();
        pending.Push(tree.GetRoot());
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is ExpressionSyntax
                && model.GetSymbolInfo(node).Symbol is { } symbol
                && !(symbol is INamespaceSymbol && QualifiesALongerName(node))
                && Banned(symbol) is { } banned)
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

        symbol = Named(symbol);
        return IsBanned(symbol) ? symbol : null;
    }

    /// <summary>What an error names for a symbol: the type for a constructor, and the declaring class for an extension.</summary>
    private static ISymbol Named(ISymbol symbol)
    {
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

        return symbol;
    }

    /// <summary>Whether the symbol is a banned type, a member of one, a banned member, or in a banned namespace.</summary>
    private static bool IsBanned(ISymbol symbol)
    {
        var type = symbol as INamedTypeSymbol ?? symbol.ContainingType;
        if (type is not null && BannedTypes.Contains(type.OriginalDefinition.ToDisplayString(Shown), StringComparer.Ordinal))
        {
            return true;
        }

        if (symbol is IPropertySymbol or IMethodSymbol && BannedMembers.Contains(symbol.ToDisplayString(Shown), StringComparer.Ordinal))
        {
            return true;
        }

        return symbol.ContainingNamespace is { } container && InBannedNamespace(container.ToDisplayString());
    }

    /// <summary>
    /// Whether <paramref name="node"/> is the left part of a dotted name, such as <c>System.Diagnostics</c> in
    /// <c>System.Diagnostics.CodeAnalysis</c>. Only used for namespaces: the whole name decides, so an allowed
    /// namespace inside a banned one isn't reported for its prefix. A type or member on the left is still checked,
    /// so <c>Environment.ProcessorCount.ToString()</c> is caught.
    /// </summary>
    private static bool QualifiesALongerName(SyntaxNode node)
    {
        // Diagnostics in System.Diagnostics names the same namespace as the whole, so climb to the whole first.
        while (node.Parent is QualifiedNameSyntax right && right.Right == node
            || node.Parent is MemberAccessExpressionSyntax member && member.Name == node)
        {
            node = node.Parent;
        }

        return node.Parent switch
        {
            QualifiedNameSyntax qualified => qualified.Left == node,
            MemberAccessExpressionSyntax access => access.Expression == node,
            AliasQualifiedNameSyntax alias => alias.Alias == node,
            _ => false,
        };
    }

    private static bool InBannedNamespace(string space)
    {
        if (AllowedNamespaces.Any(allowed => space == allowed || space.StartsWith(allowed + ".", StringComparison.Ordinal)))
        {
            return false;
        }

        return BannedNamespaces.Any(banned => space == banned || space.StartsWith(banned + ".", StringComparison.Ordinal));
    }
}
