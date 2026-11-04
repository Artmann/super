namespace Super.Cook;

/// <summary>Turns one kind of source into cooked content. Cooking must be a pure function of its input.</summary>
public interface ICooker
{
    /// <summary>The key a <c>cook.json</c> rule uses for this cooker's settings.</summary>
    string Id { get; }

    /// <summary>Bump this when the output for the same input changes, so cached results are dropped.</summary>
    int Version { get; }

    /// <summary>Lowercase, with the dot. The longest matching suffix wins.</summary>
    IReadOnlyList<string> Extensions { get; }

    void Cook(CookInput input, CookOutput output);
}
