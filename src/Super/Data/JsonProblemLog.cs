using System.Text.Json;

namespace Super;

/// <summary>
/// Writes down what's wrong with one JSON file, each problem with the file's path and the line it's on.
/// Problems go into a list shared by every file of a read, so it says whether this file added any.
/// </summary>
internal sealed class JsonProblemLog
{
    private readonly string _path;
    private readonly List<string> _problems;
    private readonly List<int> _lineStarts = [0];
    private readonly int _problemsBefore;

    public JsonProblemLog(string path, ReadOnlySpan<byte> json, List<string> problems)
    {
        _path = path;
        _problems = problems;
        _problemsBefore = problems.Count;
        for (int i = 0; i < json.Length; i++)
        {
            if (json[i] == (byte)'\n')
            {
                _lineStarts.Add(i + 1);
            }
        }
    }

    /// <summary>Whether this file has added no problems.</summary>
    public bool Passed => _problems.Count == _problemsBefore;

    /// <summary>Strips the serializer's position off its message, since the place is already named.</summary>
    public static string Plain(string message)
    {
        int position = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        int path = message.IndexOf(" Path:", StringComparison.Ordinal);
        int end = path >= 0 ? path : position;
        return end >= 0 ? message[..end].TrimEnd(' ', '.', '|') : message;
    }

    /// <summary>A field's name inside another: <c>a.b</c>, or just the name at the top of the file.</summary>
    public static string FieldPath(string field, string name) => field.Length > 0 ? $"{field}.{name}" : name;

    public void Report(int line, string problem) => _problems.Add($"{_path}:{line}: {problem}");

    /// <summary>The line, from 1, that a byte of the file is on.</summary>
    public int LineOf(long offset)
    {
        int found = _lineStarts.BinarySearch((int)offset);
        return found >= 0 ? found + 1 : ~found;
    }

    /// <summary>Where a byte of the file is, as the path and its line.</summary>
    public string PlaceOf(long offset) => $"{_path}:{LineOf(offset)}";

    /// <summary>Reports that the value the reader is on must be something else, and skips past it.</summary>
    public void Wrong(ref Utf8JsonReader reader, string field, string word)
    {
        string what = field.Length > 0 ? field : "this file";
        Report(LineOf(reader.TokenStartIndex), $"{what} must be {word}");
        reader.Skip();
    }
}
