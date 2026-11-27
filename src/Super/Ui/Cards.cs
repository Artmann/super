namespace Super.Ui;

/// <summary>
/// A big title in the middle of the screen, such as a level's name as it starts or "Victory", with an optional line
/// under it between two rules. It rises and fades in, and with <see cref="Seconds"/> set it fades away again once
/// that long has passed; take it off the interface after that. Put it in a full-screen layer, such as a
/// <see cref="ZStack"/> over the game. Inside <c>.Animations(false)</c> it shows at once and stays.
/// </summary>
/// <remarks>Called TitleCard, not Banner, so a game's own Banner, as the guides write one, keeps its name.</remarks>
public sealed record TitleCard(string Title) : UiView
{
    /// <summary>The smaller line under the title, such as "Chapter 2".</summary>
    public string? Subtitle { get; init; }

    /// <summary>How long it shows, coming in and going out included, or null to stay until it's taken away.</summary>
    public float? Seconds { get; init; }

    /// <summary>The size of the title in interface units.</summary>
    public float Size { get; init; } = 56f;

    public override UiView Body
    {
        get
        {
            var colors = Environment.Theme.Colors;
            float total = Showing.Total(Seconds);
            float shown = Showing.Amount(UseAnimation(total, total, Easing.Linear, from: 0f), total, Seconds, Environment.Still);
            var rule = new Rectangle().Fill(colors.Foreground with { A = 0.6f }).Frame(width: 64f * shown, height: 1.5f);
            var card = new VStack(HorizontalAlignment.Center, 10f)
            {
                new Text(Title).FontSize(Size).Weight(FontWeight.Bold).Foreground(colors.Foreground),
                Subtitle is { } subtitle
                    ? (UiView)new HStack(VerticalAlignment.Center, 14f) { rule, new Text(subtitle).TextStyle(TextStyle.Title3).Foreground(colors.Foreground), rule }
                    : null,
            };
            return card.Offset(0f, (1f - shown) * 16f).Opacity(shown);
        }
    }
}

/// <summary>
/// A card that says the player earned something, such as an achievement or a new item: "Achievement unlocked" over its
/// title, with an icon or picture beside it. It drops in from above and, with <see cref="Seconds"/> set, lifts away
/// again once that long has passed; take it off the interface after that. Place it, for one at the top middle of the
/// screen, with a frame and an alignment. Inside <c>.Animations(false)</c> it shows at once and stays.
/// </summary>
public sealed record Achievement(string Title) : UiView
{
    /// <summary>The line over the title.</summary>
    public string Caption { get; init; } = "Achievement unlocked";

    /// <summary>A line under the title, such as what it was for.</summary>
    public string? Description { get; init; }

    /// <summary>The icon in the badge, when there's no <see cref="Picture"/>.</summary>
    public IconName Icon { get; init; } = IconName.Star;

    /// <summary>A picture in place of the icon, such as the achievement's own art.</summary>
    public GameImage? Picture { get; init; }

    /// <summary>How long it shows, coming in and going out included, or null to stay until it's taken away.</summary>
    public float? Seconds { get; init; } = 5f;

    // The side of the badge.
    private const float BadgeSide = 48f;

    public override UiView Body
    {
        get
        {
            var theme = Environment.Theme;
            float total = Showing.Total(Seconds);
            float shown = Showing.Amount(UseAnimation(total, total, Easing.Linear, from: 0f), total, Seconds, Environment.Still);
            var words = new VStack(HorizontalAlignment.Leading, 2f)
            {
                new Text(Caption).TextStyle(TextStyle.Caption).Weight(FontWeight.SemiBold).Foreground(theme.Colors.Primary),
                new Text(Title).TextStyle(TextStyle.Headline),
                Description is { } description ? new Text(description).TextStyle(TextStyle.Footnote).Foreground(Tone.Muted) : null,
            };
            var row = new HStack(VerticalAlignment.Center, 14f) { Badge(theme), words, new Spacer() };
            var card = ThemePanel.On(row.Padding(16f, 12f).Frame(minWidth: 300f, maxWidth: 420f), theme.Panels.Popup, theme.Colors.Raised, box => box
                .Background(theme.Colors.Raised)
                .Border(1f, theme.Colors.Border)
                .CornerRadius(theme.RadiusLarge)
                .Shadow(theme.Shadows.Large));
            var meaning = new UiSemantics(UiRole.Toast, Title, Description);
            return new SemanticsModifier(card.Offset(0f, (shown - 1f) * 24f).Opacity(shown), meaning);
        }
    }

    private UiView Badge(Theme theme)
    {
        UiView inside = Picture is { } picture
            ? new Image(picture, ImageFit.Cover).Frame(BadgeSide, BadgeSide)
            : new Icon(Icon).Foreground(theme.Colors.PrimaryForeground).Frame(BadgeSide, BadgeSide).Background(theme.Colors.Primary);
        return inside.CornerRadius(BadgeSide / 2f);
    }
}

/// <summary>How much of a card that comes and goes by itself shows, worked out from one clock over its whole time.</summary>
internal static class Showing
{
    // How long coming in and going out take.
    private const float InSeconds = 0.45f;
    private const float OutSeconds = 0.6f;

    /// <summary>The card's whole time: <paramref name="seconds"/>, but long enough to come in and go out, or only coming in.</summary>
    public static float Total(float? seconds) => seconds is { } given ? MathF.Max(given, InSeconds + OutSeconds) : InSeconds;

    /// <summary>
    /// From 0 to 1: rising over the first moments, full, and falling over the last ones when there's an end, from the
    /// seconds <paramref name="elapsed"/> of the <paramref name="total"/>. Full at once when it's still.
    /// </summary>
    public static float Amount(float elapsed, float total, float? seconds, bool still)
    {
        if (still)
        {
            return 1f;
        }

        float coming = Ease(elapsed / InSeconds);
        float going = seconds is null ? 1f : Ease((total - elapsed) / OutSeconds);
        return MathF.Min(coming, going);
    }

    private static float Ease(float t)
    {
        float clamped = Math.Clamp(t, 0f, 1f);
        return 1f - (1f - clamped) * (1f - clamped);
    }
}
