namespace Super;

/// <summary>
/// The id of a record in another catalog. It's checked at load, so a broken one stops
/// there. Look it up with <c>catalog[reference]</c>.
/// </summary>
public readonly record struct Ref<T>(string Id)
    where T : notnull;

/// <summary>A reference found in a file, waiting to be checked.</summary>
internal sealed record ContentReference(Type Target, string Id, string Place, string Field);
