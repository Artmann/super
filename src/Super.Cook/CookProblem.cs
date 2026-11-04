namespace Super.Cook;

/// <summary>A mistake found while cooking. <see cref="ToString"/> is the line MSBuild and editors know how to link.</summary>
/// <param name="File">The full path when there is one, otherwise <c>label:name</c>.</param>
public sealed record CookProblem(string File, int? Line, string Message, bool IsError)
{
    public override string ToString()
    {
        string where = Line is { } line ? $"{File}({line})" : File;
        return $"{where}: {(IsError ? "error" : "warning")} SUPER001: {Message}";
    }
}
