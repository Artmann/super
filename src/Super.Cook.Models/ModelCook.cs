namespace Super.Cook.Models;

/// <summary>
/// What one cook of a model, or of one of its parts, works with. <see cref="Prefix"/> starts every message it gives, so
/// a part's messages name the part.
/// </summary>
internal sealed record ModelCook(
    CookInput Input, CookOutput Output, string Prefix, ImportSettings Settings, ModelTextures Textures, GraphRuns Graphs, bool IsGltf)
{
    public void Error(string message) => Output.Error(Prefix + message);

    public void Warning(string message) => Output.Warning(Prefix + message);

    public void Note(string message) => Output.Note(Prefix + message);

    /// <summary>Reports a problem with one material, named in the message.</summary>
    public void MaterialError(string material, string message) => Error($"material {material}: {message}");

    /// <summary>Warns about one material, named in the message.</summary>
    public void MaterialWarning(string material, string message) => Warning($"material {material}: {message}");

    /// <summary>The first rule whose <c>match</c> is in the material's name, or null when there is none.</summary>
    public MaterialRule? RuleFor(string materialName) =>
        Settings.Materials.FirstOrDefault(rule => materialName.Contains(rule.Match, StringComparison.OrdinalIgnoreCase));
}
