namespace Super.Ui;

/// <summary>
/// The look every built-in control takes: colours, corners, text sizes, shadows and control
/// sizes. Put one on a view with <c>.Theme(theme)</c> and everything inside takes it. Start
/// from <see cref="Dark"/> or <see cref="Light"/> and change what you need with <c>with</c>:
/// <code>Theme.Dark with { Colors = Theme.Dark.Colors with { Primary = Color.Hex("#3B82F6") } }</code>
/// </summary>
public sealed record Theme
{
    public required ThemeColors Colors { get; init; }

    public required ThemeShadows Shadows { get; init; }

    /// <summary>Corners of small things: checkboxes, badges, keys.</summary>
    public float RadiusSmall { get; init; } = 4f;

    /// <summary>Corners of controls: buttons, fields, menus.</summary>
    public float Radius { get; init; } = 6f;

    /// <summary>Corners of cards, dialogs and popovers.</summary>
    public float RadiusLarge { get; init; } = 10f;

    public Typography Typography { get; init; } = Typography.Default;

    public ControlMetrics Small { get; init; } = new(Height: 28f, Padding: 10f, FontSize: 12f, Gap: 6f);

    public ControlMetrics Regular { get; init; } = new(Height: 36f, Padding: 16f, FontSize: 14f, Gap: 8f);

    public ControlMetrics Large { get; init; } = new(Height: 44f, Padding: 22f, FontSize: 16f, Gap: 10f);

    /// <summary>Near-black greys with white as the main colour. The default.</summary>
    public static Theme Dark { get; } = new()
    {
        Colors = new ThemeColors
        {
            Background = Color.Hex("#09090B"),
            Foreground = Color.Hex("#FAFAFA"),
            Surface = Color.Hex("#18181B"),
            Raised = Color.Hex("#1F1F23"),
            Muted = Color.Hex("#27272A"),
            MutedForeground = Color.Hex("#A1A1AA"),
            Hover = Color.Hex("#2A2A2F"),
            Primary = Color.Hex("#FAFAFA"),
            PrimaryForeground = Color.Hex("#18181B"),
            Secondary = Color.Hex("#27272A"),
            SecondaryForeground = Color.Hex("#FAFAFA"),
            Destructive = Color.Hex("#DC2626"),
            DestructiveForeground = Color.Hex("#FAFAFA"),
            Success = Color.Hex("#22C55E"),
            Warning = Color.Hex("#F59E0B"),
            Border = Color.Hex("#2E2E33"),
            Input = Color.Hex("#3F3F46"),
            Ring = Color.Hex("#A1A1AA"),
            Scrim = Color.Hex("#000000", 0.6f),
        },
        Shadows = new ThemeShadows(
            Small: new Shadow(Color.Hex("#000000", 0.35f), Blur: 3f, Y: 1f),
            Medium: new Shadow(Color.Hex("#000000", 0.4f), Blur: 12f, Y: 4f),
            Large: new Shadow(Color.Hex("#000000", 0.5f), Blur: 32f, Y: 12f)),
    };

    /// <summary>White surfaces with near-black text and buttons.</summary>
    public static Theme Light { get; } = new()
    {
        Colors = new ThemeColors
        {
            Background = Color.Hex("#FFFFFF"),
            Foreground = Color.Hex("#09090B"),
            Surface = Color.Hex("#FFFFFF"),
            Raised = Color.Hex("#FFFFFF"),
            Muted = Color.Hex("#F4F4F5"),
            MutedForeground = Color.Hex("#71717A"),
            Hover = Color.Hex("#F4F4F5"),
            Primary = Color.Hex("#18181B"),
            PrimaryForeground = Color.Hex("#FAFAFA"),
            Secondary = Color.Hex("#F4F4F5"),
            SecondaryForeground = Color.Hex("#18181B"),
            Destructive = Color.Hex("#DC2626"),
            DestructiveForeground = Color.Hex("#FAFAFA"),
            Success = Color.Hex("#16A34A"),
            Warning = Color.Hex("#D97706"),
            Border = Color.Hex("#E4E4E7"),
            Input = Color.Hex("#D4D4D8"),
            Ring = Color.Hex("#71717A"),
            Scrim = Color.Hex("#000000", 0.4f),
        },
        Shadows = new ThemeShadows(
            Small: new Shadow(Color.Hex("#000000", 0.08f), Blur: 3f, Y: 1f),
            Medium: new Shadow(Color.Hex("#000000", 0.12f), Blur: 12f, Y: 4f),
            Large: new Shadow(Color.Hex("#000000", 0.18f), Blur: 32f, Y: 12f)),
    };

    /// <summary>The sizes for controls of this size.</summary>
    public ControlMetrics Metrics(ControlSize size) => size switch
    {
        ControlSize.Small => Small,
        ControlSize.Large => Large,
        _ => Regular,
    };
}

/// <summary>The colours of a <see cref="Theme"/>, named for what they're used for.</summary>
public sealed record ThemeColors
{
    /// <summary>Behind everything: a full-screen menu or a window.</summary>
    public required Color Background { get; init; }

    /// <summary>Text and icons on <see cref="Background"/> and <see cref="Surface"/>.</summary>
    public required Color Foreground { get; init; }

    /// <summary>Panels and cards that sit on the game or the background.</summary>
    public required Color Surface { get; init; }

    /// <summary>What floats above a surface: menus, popovers, dialogs and toasts.</summary>
    public required Color Raised { get; init; }

    /// <summary>Quiet fills, such as a slider's track or a progress bar's empty part.</summary>
    public required Color Muted { get; init; }

    /// <summary>Text that matters less: hints, captions, placeholders.</summary>
    public required Color MutedForeground { get; init; }

    /// <summary>What a row or a quiet button turns when the pointer is over it.</summary>
    public required Color Hover { get; init; }

    /// <summary>The main action: a primary button, a switch that's on, a slider's fill.</summary>
    public required Color Primary { get; init; }

    public required Color PrimaryForeground { get; init; }

    /// <summary>A button that's there but isn't the main action.</summary>
    public required Color Secondary { get; init; }

    public required Color SecondaryForeground { get; init; }

    /// <summary>Actions that destroy something, and errors.</summary>
    public required Color Destructive { get; init; }

    public required Color DestructiveForeground { get; init; }

    public required Color Success { get; init; }

    public required Color Warning { get; init; }

    /// <summary>Lines between things, and the edges of cards.</summary>
    public required Color Border { get; init; }

    /// <summary>The edge of something the player types into or picks from.</summary>
    public required Color Input { get; init; }

    /// <summary>The ring around what keyboard or gamepad focus is on.</summary>
    public required Color Ring { get; init; }

    /// <summary>Laid over everything behind a dialog.</summary>
    public required Color Scrim { get; init; }

    /// <summary>The colour a tone stands for.</summary>
    public Color this[Tone tone] => tone switch
    {
        Tone.Muted => MutedForeground,
        Tone.Primary => Primary,
        Tone.Destructive => Destructive,
        Tone.Success => Success,
        Tone.Warning => Warning,
        _ => Foreground,
    };
}

/// <summary>The three shadows of a <see cref="Theme"/>: under a button, a card, and a dialog.</summary>
public sealed record ThemeShadows(Shadow Small, Shadow Medium, Shadow Large)
{
    /// <summary>The shadow of this size.</summary>
    public Shadow this[ShadowSize size] => size switch
    {
        ShadowSize.Small => Small,
        ShadowSize.Large => Large,
        _ => Medium,
    };
}

/// <summary>A colour from the theme, for text and icons, so they follow a change of theme.</summary>
public enum Tone
{
    /// <summary>The theme's <see cref="ThemeColors.Foreground"/>.</summary>
    Default,

    /// <summary>The theme's <see cref="ThemeColors.MutedForeground"/>, for what matters less.</summary>
    Muted,

    Primary,
    Destructive,
    Success,
    Warning,
}

/// <summary>A soft shadow under a box: its colour, how far it blurs, and how far it falls.</summary>
public readonly record struct Shadow(Color Color, float Blur, float Y = 0f, float X = 0f, float Spread = 0f);

/// <summary>How far something seems to float, by the theme's shadow under it: a button, a card, or a dialog.</summary>
public enum ShadowSize
{
    Small,
    Medium,
    Large,
}

/// <summary>How big a control is: its height, the room at its sides, its text size, and the gap between an icon and its label.</summary>
public readonly record struct ControlMetrics(float Height, float Padding, float FontSize, float Gap);

public enum ControlSize
{
    Small,
    Regular,
    Large,
}
