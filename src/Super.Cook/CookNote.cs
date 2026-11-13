namespace Super.Cook;

/// <summary>
/// Something a cooker did that the user may want to know about but didn't do wrong, such as making a second UV set a
/// model lacked. A note isn't a problem: it never fails a cook, isn't counted as a warning and isn't kept in the cache,
/// so it shows only on the cook that did the work. <see cref="ToString"/> has no <c>error</c> or <c>warning</c> in it,
/// so MSBuild prints it as a plain message.
/// </summary>
/// <param name="File">The full path when there is one, otherwise <c>label:name</c>.</param>
public sealed record CookNote(string File, string Message)
{
    public override string ToString() => $"{File}: note: {Message}";
}
