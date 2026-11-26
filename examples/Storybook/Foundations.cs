using Super;
using Super.Ui;
using static StorybookExample.Layout;

namespace StorybookExample;

/// <summary>The stories for what everything else is built from: text, colour, shadow, shape, gradient and icons.</summary>
internal static class Foundations
{
    private const string Sample = "The quick brown fox jumps over the lazy dog";

    public static UiView Typography() => Page(
        TextStyles(),
        Weights(),
        Alignments(),
        new Specimen(
            "Line limits",
            "Text cut short ends in an ellipsis.",
            new VStack(HorizontalAlignment.Leading, 10f)
            {
                new Text($"{Sample}, again and again, until it runs out of room.").LineLimit(1).Frame(width: 300f, alignment: Alignment.Leading),
                new Text($"{Sample}, again and again, until it runs out of room on the second line.").LineLimit(2).Frame(width: 300f, alignment: Alignment.Leading),
            }),
        new Specimen(
            "Tones",
            "Colours from the theme, so they follow a change of theme.",
            Row(
                20f,
                new Text("Default").Foreground(Tone.Default),
                new Text("Muted").Foreground(Tone.Muted),
                new Text("Primary").Foreground(Tone.Primary),
                new Text("Destructive").Foreground(Tone.Destructive),
                new Text("Success").Foreground(Tone.Success),
                new Text("Warning").Foreground(Tone.Warning))),
        new Specimen(
            "Digits",
            "Monospaced digits keep a ticking number from shaking.",
            Row(
                32f,
                Captioned(new Text("1,111 / 8,808").FontSize(20f), "Proportional"),
                Captioned(new Text("1,111 / 8,808").FontSize(20f).MonospacedDigit(), "Monospaced"))));

    private static Specimen TextStyles()
    {
        var styles = new VStack(HorizontalAlignment.Leading, 14f);
        foreach (var style in Enum.GetValues<TextStyle>())
        {
            styles.Add(Row(
                16f,
                new Text(style.ToString()).TextStyle(TextStyle.Footnote).Foreground(Tone.Muted).Frame(width: 96f, alignment: Alignment.Leading),
                new Text(Sample).TextStyle(style).LineLimit(1)));
        }

        return new Specimen("Text styles", "Each style takes its size and weight from the theme's typography.", styles);
    }

    private static Specimen Weights()
    {
        var weights = new HStack(Spacing: 28f);
        foreach (var weight in Enum.GetValues<FontWeight>())
        {
            weights.Add(Captioned(new Text("Aa").FontSize(32f).Weight(weight), weight.ToString()));
        }

        weights.Add(Captioned(new Text("Aa").FontSize(32f).Italic(), "Italic"));
        return new Specimen("Weights", "Regular, medium, semibold and bold Inter, and italic.", weights);
    }

    private static Specimen Alignments()
    {
        const string paragraph = "Lines that differ in length show where text sits in its frame. This one is aligned to the";
        return new Specimen(
            "Alignment",
            "Each line of a paragraph sits at the leading edge, in the middle, or at the trailing edge.",
            Row(
                24f,
                new Text($"{paragraph} leading edge.").Frame(width: 220f),
                new Text($"{paragraph} middle.").TextAlign(TextAlignment.Center).Frame(width: 220f),
                new Text($"{paragraph} trailing edge.").TextAlign(TextAlignment.Trailing).Frame(width: 220f)));
    }

    public static UiView Colours() => Page(
        new Specimen("Surfaces", "What screens, panels and popups are made of.", new Swatches(Swatches.Surfaces)),
        new Specimen("Actions", "Buttons, switches and sliders, with the text that sits on them.", new Swatches(Swatches.Actions)),
        new Specimen("Status", "For what went right, what needs care, and what went wrong.", new Swatches(Swatches.Statuses)),
        new Specimen("Lines", "Edges, fields and the focus ring.", new Swatches(Swatches.Lines)));

    public static UiView Shadows() => Page(
        new Specimen(
            "Theme shadows",
            "Small for buttons, medium for cards and menus, large for dialogs. They follow the theme.",
            Row(40f, Card(ShadowSize.Small), Card(ShadowSize.Medium), Card(ShadowSize.Large)).Padding(16f)),
        new Specimen(
            "Your own",
            "A colour, a blur, an offset and a spread. A glow is a shadow that doesn't move.",
            Row(
                40f,
                Captioned(new Spacer().Frame(120f, 80f).Background(Color.Hex("#6366F1")).CornerRadius(12f).Shadow(Color.Hex("#6366F1", 0.6f), blur: 28f), "Glow"),
                Captioned(new Spacer().Frame(120f, 80f).Background(Color.Hex("#F4F4F5")).CornerRadius(12f).Shadow(Color.Hex("#000000", 0.5f), blur: 0f, x: 6f, y: 6f), "Hard"),
                Captioned(new Spacer().Frame(120f, 80f).Background(Color.Hex("#F59E0B")).CornerRadius(40f).Shadow(Color.Hex("#F59E0B", 0.45f), blur: 18f, y: 10f, spread: -6f), "Lifted")).Padding(16f)));

    public static UiView Shapes()
    {
        var primary = Color.Hex("#6366F1");
        return Page(
            new Specimen(
                "Filled",
                "A shape fills its frame with the text colour unless told otherwise.",
                Row(
                    24f,
                    Captioned(new Rectangle().Frame(72f, 72f), "Rectangle"),
                    Captioned(new RoundedRectangle(16f).Frame(72f, 72f), "Rounded"),
                    Captioned(new Capsule().Frame(120f, 40f), "Capsule"),
                    Captioned(new Circle().Frame(72f, 72f), "Circle"),
                    Captioned(new Ellipse().Frame(110f, 64f), "Ellipse"))),
            new Specimen(
                "Stroked",
                "A stroke sits inside the edge, so it never spills out of the frame.",
                Row(
                    24f,
                    new RoundedRectangle(12f).Stroke(primary, 2f).Frame(72f, 72f),
                    new Capsule().Stroke(primary, 2f).Frame(120f, 40f),
                    new Circle().Stroke(primary, 3f).Frame(72f, 72f),
                    new RoundedRectangle(12f).Fill(primary.Mix(Color.Black, 0.6f)).Stroke(primary, 2f).Frame(72f, 72f))),
            new Specimen(
                "Corners of their own",
                "Each corner can be rounded by its own amount, such as a tab's top or a speech bubble.",
                Row(
                    24f,
                    Captioned(new RoundedRectangle(new CornerRadii(16f, 16f, 0f, 0f)).Fill(primary).Frame(96f, 56f), "Tab"),
                    Captioned(new RoundedRectangle(new CornerRadii(18f, 18f, 18f, 4f)).Fill(primary).Frame(120f, 56f), "Bubble"),
                    Captioned(new RoundedRectangle(new CornerRadii(28f, 0f, 28f, 0f)).Fill(primary).Frame(72f, 72f), "Leaf"))),
            new Specimen(
                "Arcs",
                "Part of a ring with round ends, for progress, cooldowns and gauges.",
                Row(
                    28f,
                    Captioned(Ring(0.25f, primary), "25%"),
                    Captioned(Ring(0.6f, Color.Hex("#22C55E")), "60%"),
                    Captioned(Ring(0.9f, Color.Hex("#F59E0B")), "90%"),
                    Captioned(new Arc(0.625f, 0.75f, Thickness: 8f).Fill(Color.Hex("#EF4444")).Frame(64f, 64f), "Gauge"))));
    }

    public static UiView Gradients()
    {
        var violet = Color.Hex("#7C3AED");
        var pink = Color.Hex("#EC4899");
        var sky = Color.Hex("#0EA5E9");
        var night = Color.Hex("#0F172A");
        return Page(
            new Specimen(
                "Linear",
                "At an angle, as CSS has it: 180 runs top to bottom, 90 left to right.",
                Row(
                    20f,
                    Captioned(Swatch(Paint.Linear(violet, pink, 90f)), "90°"),
                    Captioned(Swatch(Paint.Linear(violet, pink, 135f)), "135°"),
                    Captioned(Swatch(Paint.Linear(violet, pink)), "180°"),
                    Captioned(Swatch(Paint.Linear(sky, violet, Alignment.BottomLeading, Alignment.TopTrailing)), "Corner to corner"))),
            new Specimen(
                "Radial",
                "Out from a point, reaching the farthest corner unless told how far.",
                Row(
                    20f,
                    Captioned(Swatch(Paint.Radial(pink, violet)), "Centre"),
                    Captioned(Swatch(Paint.Radial(sky, night, Alignment.TopLeading)), "Corner"),
                    Captioned(Swatch(Paint.Radial(Color.Hex("#FDE68A"), night, radius: 40f)), "40 units"),
                    Captioned(new Circle().Fill(Paint.Radial(Color.Hex("#FDE68A"), Color.Hex("#F97316"), Alignment.TopLeading)).Frame(96f, 96f), "On a shape"))),
            new Specimen(
                "Put together",
                "A banner with a gradient behind its text and a shadow in its colour.",
                new VStack(HorizontalAlignment.Leading, 6f)
                {
                    new Text("Season 3").TextStyle(TextStyle.Caption).Foreground(Color.Hex("#FFFFFF", 0.8f)),
                    new Text("The Sunken Archive").TextStyle(TextStyle.Title).Foreground(Color.White),
                    new Text("New dungeon, new loot, and a boss who reads.").Foreground(Color.Hex("#FFFFFF", 0.85f)),
                }
                .Padding(28f, 24f)
                .Frame(width: 460f, alignment: Alignment.Leading)
                .Background(Paint.Linear(violet, pink, 120f))
                .CornerRadius(16f)
                .Shadow(Color.Hex("#EC4899", 0.35f), blur: 24f, y: 10f)));
    }

    public static UiView Icons()
    {
        var grid = new VStack(HorizontalAlignment.Leading, 18f);
        var names = Enum.GetValues<IconName>();
        for (int start = 0; start < names.Length; start += 8)
        {
            var row = new HStack(VerticalAlignment.Top, 0f);
            foreach (var name in names.Skip(start).Take(8))
            {
                row.Add(Captioned(new Icon(name).FontSize(24f), name.ToString()).Frame(width: 96f));
            }

            grid.Add(row);
        }

        return Page(
            new Specimen("Every icon", "Drawn as lines, so they're sharp at any size.", grid),
            new Specimen(
                "Sizes",
                "An icon is a little bigger than the text it sits in.",
                Row(24f, new Icon(IconName.Search).FontSize(12f), new Icon(IconName.Search).FontSize(16f), new Icon(IconName.Search).FontSize(24f), new Icon(IconName.Search).FontSize(40f))),
            new Specimen(
                "Colours",
                "It takes the text colour, so a tone works on it.",
                Row(
                    20f,
                    new Icon(IconName.Info).FontSize(24f),
                    new Icon(IconName.Info).FontSize(24f).Foreground(Tone.Muted),
                    new Icon(IconName.Check).FontSize(24f).Foreground(Tone.Success),
                    new Icon(IconName.Info).FontSize(24f).Foreground(Tone.Warning),
                    new Icon(IconName.Close).FontSize(24f).Foreground(Tone.Destructive))));
    }

    private static UiView Card(ShadowSize size) => Captioned(new ShadowCard(size), $".Shadow(ShadowSize.{size})");

    private static UiView Ring(float progress, Color color) => new ZStack
    {
        new Circle().Stroke(color.Mix(Color.Black, 0.65f), 6f).Frame(64f, 64f),
        new Arc(0f, progress, Thickness: 6f).Fill(color).Frame(64f, 64f),
    };

    private static UiView Swatch(Paint paint) => new RoundedRectangle(12f).Fill(paint).Frame(140f, 96f);
}

/// <summary>A card in the theme's raised colour, casting one of the theme's shadows.</summary>
internal sealed record ShadowCard(ShadowSize Size) : UiView
{
    public override UiView Body
    {
        get
        {
            var theme = Environment.Theme;
            return new Text(Size.ToString()).TextStyle(TextStyle.Subheadline)
                .Frame(140f, 90f)
                .Background(theme.Colors.Raised)
                .Border(1f, theme.Colors.Border)
                .CornerRadius(theme.RadiusLarge)
                .Shadow(Size);
        }
    }
}

/// <summary>Rows of the theme's colours, each a chip with its name under it.</summary>
internal sealed record Swatches(Func<Theme, (string Name, Color Color)[]> Pick) : UiView
{
    public static (string, Color)[] Surfaces(Theme theme) =>
    [
        ("Background", theme.Colors.Background), ("Surface", theme.Colors.Surface), ("Raised", theme.Colors.Raised), ("Muted", theme.Colors.Muted),
        ("Hover", theme.Colors.Hover), ("Foreground", theme.Colors.Foreground), ("MutedForeground", theme.Colors.MutedForeground),
    ];

    public static (string, Color)[] Actions(Theme theme) =>
    [
        ("Primary", theme.Colors.Primary), ("PrimaryForeground", theme.Colors.PrimaryForeground), ("Secondary", theme.Colors.Secondary),
        ("SecondaryForeground", theme.Colors.SecondaryForeground), ("Destructive", theme.Colors.Destructive), ("DestructiveForeground", theme.Colors.DestructiveForeground),
    ];

    public static (string, Color)[] Statuses(Theme theme) => [("Success", theme.Colors.Success), ("Warning", theme.Colors.Warning), ("Destructive", theme.Colors.Destructive)];

    public static (string, Color)[] Lines(Theme theme) => [("Border", theme.Colors.Border), ("Input", theme.Colors.Input), ("Ring", theme.Colors.Ring), ("Scrim", theme.Colors.Scrim)];

    public override UiView Body
    {
        get
        {
            var theme = Environment.Theme;
            var row = new HStack(VerticalAlignment.Top, 14f);
            foreach (var (name, color) in Pick(theme))
            {
                row.Add(new VStack(HorizontalAlignment.Leading, 6f)
                {
                    new RoundedRectangle(theme.Radius).Fill(color).Stroke(theme.Colors.Border, 1f).Frame(104f, 56f),
                    new Text(name).TextStyle(TextStyle.Caption).LineLimit(1).Frame(width: 104f, alignment: Alignment.Leading),
                });
            }

            return row;
        }
    }
}
