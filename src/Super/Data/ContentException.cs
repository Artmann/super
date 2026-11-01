namespace Super;

/// <summary>What was wrong with some JSON content, one line per problem.</summary>
public sealed class ContentException(IReadOnlyList<string> problems) : Exception(string.Join(Environment.NewLine, problems))
{
    /// <summary>Each problem, as file, line and what to fix.</summary>
    public IReadOnlyList<string> Problems { get; } = problems;
}
