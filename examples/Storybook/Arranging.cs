using Super.Ui;
using static StorybookExample.Layout;

namespace StorybookExample;

/// <summary>The stories for laying views out: layers and offsets, grids, flows, views that fit, and lazy lists.</summary>
internal static class Arranging
{
    private static readonly string[] Tags =
    [
        "Fire resistant", "Two-handed", "Bound", "Cursed", "Repaired", "Set: Ember", "Level 40", "Sells for 1,250", "Quest item",
    ];

    private static readonly string[] Players = Enumerable.Range(1, 10_000).Select(rank => $"Player {rank:N0}").ToArray();

    public static UiView Layers() => Page(
        new Specimen(
            "Overlay and background",
            "An overlay sits in front of a view and a background behind it, and neither changes its size. The count rides the corner of the slot.",
            Row(
                24f,
                Slot("Potions").Overlay(new Badge("12").Offset(8f, -8f), Alignment.TopTrailing),
                Slot("Arrows").Overlay(new Badge("99+", BadgeStyle.Destructive).Offset(8f, -8f), Alignment.TopTrailing),
                new Text("Behind").Padding(20f, 12f).Background(new Capsule().Foreground(Tone.Primary).Opacity(0.3f)))),
        new Specimen(
            "Offset",
            "An offset moves where a view is drawn and clicked, and leaves the views round it where they were.",
            Row(12f, new Button("One", () => { }), new Button("Two, lifted", () => { }).Offset(y: -10f), new Button("Three", () => { }))),
        new Specimen(
            "Hidden",
            "A hidden view keeps its room, so nothing shifts when it comes back. Leave a view out with null to take the room too.",
            new Live<bool>(true, (hidden, set) => new VStack(HorizontalAlignment.Leading, 12f)
            {
                new Toggle("Hide the middle one", hidden, set).Id("hide"),
                Row(12f, new Badge("First"), new Badge("Middle", BadgeStyle.Secondary).Hidden(hidden), new Badge("Last")),
            })),
        new Specimen(
            "Aspect ratio",
            "A shape takes the biggest size of its ratio that fits, or with Fill the smallest that covers the room.",
            Row(
                56f,
                Captioned(new Outlined(new RoundedRectangle(8f).Foreground(Tone.Primary).AspectRatio(16f / 9f).Frame(160f, 120f), 0f), "16:9 fit"),
                Captioned(new Outlined(new RoundedRectangle(8f).Foreground(Tone.Primary).AspectRatio(1f).Frame(160f, 120f), 0f), "1:1 fit"),
                Captioned(new Outlined(new RoundedRectangle(8f).Foreground(Tone.Primary).Opacity(0.5f).AspectRatio(16f / 9f, ContentMode.Fill).Frame(160f, 120f), 0f), "16:9 fill"))));

    public static UiView Grids() => Page(
        new Specimen(
            "Rows",
            "Each column is as wide as its widest cell, so the labels line up. A slider stretches into the room left, and a divider spans the grid.",
            new Grid(ColumnGap: 16f, RowGap: 12f)
            {
                new GridRow { new Text("Master volume"), new Slider(0.8f, _ => { }), new Text("80%").Foreground(Tone.Muted) },
                new GridRow { new Text("Music"), new Slider(0.5f, _ => { }), new Text("50%").Foreground(Tone.Muted) },
                new Divider(),
                new GridRow { new Text("Voice chat"), new Slider(0.25f, _ => { }), new Text("25%").Foreground(Tone.Muted) },
            }.Frame(width: 480f)),
        new Specimen(
            "Equal columns",
            "Give Columns and no rows, and the cells fill the columns left to right and then down.",
            Inventory()));

    public static UiView Flows() => Page(
        new Specimen(
            "Flow stack",
            "Children sit in a row and wrap onto the next line when they don't fit, like words.",
            Chips(HorizontalAlignment.Leading).Frame(width: 420f, alignment: Alignment.Leading)),
        new Specimen(
            "Centred lines",
            "Each line can sit at the start, the middle or the end.",
            Chips(HorizontalAlignment.Center).Frame(width: 420f)),
        new Specimen(
            "View that fits",
            "The first child that fits is shown: the full label, then a short one, then the icon alone.",
            new VStack(HorizontalAlignment.Leading, 12f)
            {
                Saving(320f),
                Saving(110f),
                Saving(40f),
            }),
        new Specimen(
            "Layout priority",
            "When a row is short of room, the higher priority gets its line first and the others wrap.",
            new VStack(HorizontalAlignment.Leading, 16f)
            {
                Captioned(Priorities(first: 0f), "Equal priorities"),
                Captioned(Priorities(first: 1f), "The name first"),
            }));

    public static UiView LazyLists() => Page(
        new Specimen(
            "Ten thousand rows",
            "A lazy stack makes only the rows near the screen, so a long leaderboard costs what a short one does. Scroll it, or focus a row and hold Down.",
            new ScrollView
            {
                new LazyVStack<string>(Players, player => Ranked(player), RowHeight: 36f, Spacing: 2f),
            }.Frame(width: 420f, height: 300f)));

    private static UiView Slot(string name) => new Outlined(new Text(name).Frame(96f, 64f), 8f);


    private static UiView Inventory()
    {
        var grid = new Grid(Columns: 6, ColumnGap: 8f);
        for (int i = 0; i < 12; i++)
        {
            grid.Add(i % 5 == 3 ? null : new Outlined(new Text($"{i + 1}").Foreground(Tone.Muted).Frame(48f, 48f), 6f));
        }

        return grid.Frame(width: 6 * 48f + 5 * 8f, alignment: Alignment.Leading);
    }

    private static FlowStack Chips(HorizontalAlignment alignment)
    {
        var flow = new FlowStack(Spacing: 8f, LineSpacing: 8f, Alignment: alignment);
        foreach (var tag in Tags)
        {
            flow.Add(new Badge(tag, BadgeStyle.Secondary));
        }

        return flow;
    }

    private static UiView Saving(float width) => new ViewThatFits
    {
        new Label("Saving your progress", IconName.Check),
        new Label("Saving", IconName.Check),
        new Icon(IconName.Check),
    }.Frame(width: width, alignment: Alignment.Leading);

    private static UiView Priorities(float first) => new HStack(VerticalAlignment.Top, 16f)
    {
        new Text("Commander Aldric Vane of the Ember Guard").TextStyle(TextStyle.Headline).LayoutPriority(first),
        new Text("Last seen in the eastern marshes, two days ago").Foreground(Tone.Muted),
    }.Frame(width: 460f, alignment: Alignment.Leading);

    private static UiView Ranked(string player) => new Button(player, () => { }) { Style = ButtonStyle.Ghost }
        .Frame(maxWidth: UiExtensions.Infinity, alignment: Alignment.Leading);
}

/// <summary>A view with a thin line round it in the theme's border colour.</summary>
internal sealed record Outlined(UiView Content, float Radius) : UiView
{
    public override UiView Body => Content.Border(1f, Environment.Theme.Colors.Border).CornerRadius(Radius);
}
