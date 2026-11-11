namespace Rendition;

/// <summary>How long a bake takes over its light: a quick look while working, or the one that ships.</summary>
public enum BakeQuality
{
    Preview,
    Final,
}

/// <summary>
/// What a bake is asked for beyond the scene itself. A bake made with another texel density is stale; one made with
/// another quality is not, since quality decides how long a bake runs and not what it shows.
/// </summary>
public readonly record struct BakeSettings
{
    public const int DefaultTexelsPerMetre = 20;
    public const int MinTexelsPerMetre = 1;
    public const int MaxTexelsPerMetre = 200;

    // Stored as an offset from the default, so default(BakeSettings) means 20 texels a metre and not 0.
    private readonly int _texelsOffset;

    // The same for quality: false is Final, so default(BakeSettings) is the same as new BakeSettings().
    private readonly bool _preview;

    public BakeSettings(int texelsPerMetre = DefaultTexelsPerMetre, BakeQuality quality = BakeQuality.Final)
    {
        if (texelsPerMetre < MinTexelsPerMetre || texelsPerMetre > MaxTexelsPerMetre)
        {
            throw new ArgumentOutOfRangeException(
                nameof(texelsPerMetre), texelsPerMetre,
                $"texelsPerMetre must be {MinTexelsPerMetre} to {MaxTexelsPerMetre}, not {texelsPerMetre}.");
        }

        _texelsOffset = texelsPerMetre - DefaultTexelsPerMetre;
        _preview = quality == BakeQuality.Preview;
    }

    /// <summary>Texels in a lightmap for each metre of surface, 1 to 200. 20 unless asked for otherwise.</summary>
    public int TexelsPerMetre => _texelsOffset + DefaultTexelsPerMetre;

    /// <summary>How long the bake runs. It isn't part of what makes a bake fresh.</summary>
    public BakeQuality Quality => _preview ? BakeQuality.Preview : BakeQuality.Final;
}

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
