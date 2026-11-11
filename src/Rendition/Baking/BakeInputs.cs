namespace Rendition;

/// <summary>How long a bake takes over its light: a quick look while working, or the one that ships.</summary>
public enum BakeQuality
{
    Preview,
    Final,
}

/// <summary>What a bake is asked for beyond the scene itself. A bake made with other settings is stale.</summary>
public readonly record struct BakeSettings(int TexelsPerMetre = 20, BakeQuality Quality = BakeQuality.Final);

/// <summary>
/// The four hashes a baked space or chunk is made from, kept beside its bake so a frame can tell whether the bake is
/// fresh. See "Is the bake fresh?" in docs/design.md.
/// </summary>
/// <param name="Draws">The area's static draws, by their sorted <see cref="DrawItem.BakeId"/>s.</param>
/// <param name="Lights">The area's Mixed and Baked lights, sorted, and for a space its sun.</param>
/// <param name="Environment">The renderer's <c>EnvironmentHash</c> for a space; zero for a chunk.</param>
/// <param name="Settings">The bake settings, the baker's version and the area's box.</param>
public readonly record struct BakeInputs(ulong Draws, ulong Lights, ulong Environment, ulong Settings)
{
    /// <summary>One hash of all four, in the order they're declared: the one a bake is matched by.</summary>
    public ulong Combined => BakeInputHash.Hasher().Add(Draws).Add(Lights).Add(Environment).Add(Settings).Finish();

    /// <summary>
    /// The parts that differ from <paramref name="before"/>, always in the order "draws", "lights", "environment",
    /// "settings". Empty when the bake is fresh.
    /// </summary>
    public IReadOnlyList<string> Changes(BakeInputs before)
    {
        var changes = new List<string>(4);
        if (Draws != before.Draws)
        {
            changes.Add("draws");
        }

        if (Lights != before.Lights)
        {
            changes.Add("lights");
        }

        if (Environment != before.Environment)
        {
            changes.Add("environment");
        }

        if (Settings != before.Settings)
        {
            changes.Add("settings");
        }

        return changes;
    }
}
