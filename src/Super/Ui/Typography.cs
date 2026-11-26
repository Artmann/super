namespace Super.Ui;

/// <summary>How heavy text is. Fonts of your own have a regular and a bold file, so Medium is drawn regular and SemiBold bold.</summary>
public enum FontWeight
{
    Regular,
    Medium,
    SemiBold,
    Bold,
}

/// <summary>Where each line of text sits in its frame when lines differ in length.</summary>
public enum TextAlignment
{
    Leading,
    Center,
    Trailing,
}

/// <summary>The kind of text something is, sized and weighted by the theme's <see cref="Typography"/>.</summary>
public enum TextStyle
{
    LargeTitle,
    Title,
    Title2,
    Title3,
    Headline,
    Body,
    Callout,
    Subheadline,
    Footnote,
    Caption,
}

/// <summary>A text style's size in pixels and weight.</summary>
public readonly record struct TypeFace(float Size, FontWeight Weight = FontWeight.Regular);

/// <summary>The size and weight of each <see cref="TextStyle"/>.</summary>
public sealed record Typography
{
    // Each style's face, in the order of TextStyle.
    private static readonly Func<Typography, TypeFace>[] Faces =
    [
        type => type.LargeTitle,
        type => type.Title,
        type => type.Title2,
        type => type.Title3,
        type => type.Headline,
        type => type.Body,
        type => type.Callout,
        type => type.Subheadline,
        type => type.Footnote,
        type => type.Caption,
    ];

    public TypeFace LargeTitle { get; init; } = new(30f, FontWeight.Bold);

    public TypeFace Title { get; init; } = new(24f, FontWeight.SemiBold);

    public TypeFace Title2 { get; init; } = new(20f, FontWeight.SemiBold);

    public TypeFace Title3 { get; init; } = new(17f, FontWeight.SemiBold);

    public TypeFace Headline { get; init; } = new(14f, FontWeight.SemiBold);

    public TypeFace Body { get; init; } = new(14f);

    public TypeFace Callout { get; init; } = new(13f);

    public TypeFace Subheadline { get; init; } = new(13f, FontWeight.Medium);

    public TypeFace Footnote { get; init; } = new(12f);

    public TypeFace Caption { get; init; } = new(11f, FontWeight.Medium);

    public static Typography Default { get; } = new();

    /// <summary>The face of this style, or <see cref="Body"/>'s for one that isn't a style.</summary>
    public TypeFace this[TextStyle style] => (int)style >= 0 && (int)style < Faces.Length ? Faces[(int)style](this) : Body;
}
