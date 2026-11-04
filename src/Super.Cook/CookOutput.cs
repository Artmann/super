namespace Super.Cook;

/// <summary>Where a cooker puts what it made, and its mistakes.</summary>
public sealed class CookOutput
{
    private readonly List<(string? Part, ContentKind Kind, byte[] Bytes)> _assets = [];
    private readonly List<(string Message, int? Line, bool IsError)> _problems = [];

    internal IReadOnlyList<(string? Part, ContentKind Kind, byte[] Bytes)> Assets => _assets;

    internal IReadOnlyList<(string Message, int? Line, bool IsError)> Problems => _problems;

    /// <summary>Adds the asset the source stands for, under the source's own name.</summary>
    public void Add(ContentKind kind, byte[] bytes)
    {
        _assets.Add((null, kind, bytes));
    }

    /// <summary>Adds one piece of a source, named <c>source#part</c>.</summary>
    public void AddPart(string part, ContentKind kind, byte[] bytes)
    {
        _assets.Add((part, kind, bytes));
    }

    public void Error(string message, int? line = null)
    {
        _problems.Add((message, line, true));
    }

    public void Warning(string message, int? line = null)
    {
        _problems.Add((message, line, false));
    }
}
