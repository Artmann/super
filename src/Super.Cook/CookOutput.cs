using System.Security.Cryptography;

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

    /// <summary>
    /// Adds bytes that assets point to by their hash, such as a texture several models share, and returns
    /// that hash. The object has no name, so the content keeps one copy however many sources add it.
    /// </summary>
    public string AddObject(byte[] bytes) => AddObject(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)));

    /// <summary>Adds an object whose hash the cooker already knows, such as one it made before.</summary>
    /// <param name="hash">The SHA-256 of <paramref name="bytes"/> in lowercase hex.</param>
    internal string AddObject(byte[] bytes, string hash)
    {
        if (_objectHashes.Add(hash))
        {
            _objects.Add((hash, bytes));
        }

        return hash;
    }

    internal IReadOnlyList<(string Hash, byte[] Bytes)> Objects => _objects;

    private readonly List<(string Hash, byte[] Bytes)> _objects = [];
    private readonly HashSet<string> _objectHashes = new(StringComparer.Ordinal);

    public void Error(string message, int? line = null)
    {
        _problems.Add((message, line, true));
    }

    public void Warning(string message, int? line = null)
    {
        _problems.Add((message, line, false));
    }
}
