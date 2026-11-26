using Super.Ui;
using static StorybookExample.Layout;

namespace StorybookExample;

/// <summary>The stories for views that hold others: cards, forms and their sections, disclosure groups, tabs and navigation.</summary>
internal static class Containing
{
    private enum SettingsPage
    {
        Graphics,
        Advanced,
        Audio,
    }

    public static UiView Cards() => Page(
        new Specimen(
            "Card",
            "A raised panel for a group of things that belong together, such as a quest or a character's stats.",
            Row(
                24f,
                new Card
                {
                    new Text("The Ember Crown").TextStyle(TextStyle.Headline),
                    new Text("Bring the crown back from the burning keep before the third night.").Foreground(Tone.Muted),
                    new HStack(Spacing: 8f) { new Badge("Main quest"), new Badge("Level 12", BadgeStyle.Secondary) },
                    new Button("Track quest", () => { }),
                }.Frame(width: 300f),
                new Card
                {
                    new Text("Arin, the ranger").TextStyle(TextStyle.Headline),
                    new LabeledContent("Health", "320 / 320"),
                    new LabeledContent("Stamina", "84"),
                    new LabeledContent("Gold", "1,250"),
                }.Frame(width: 240f))),
        new Specimen(
            "Labeled content",
            "A label with a value or a control at the trailing edge of the row. Text given as the value is muted.",
            new VStack(HorizontalAlignment.Leading, 12f)
            {
                new LabeledContent("Play time", "41 h 12 min"),
                new LabeledContent("Difficulty", new Badge("Hard", BadgeStyle.Destructive)),
                new LabeledContent("Cloud saves", new Toggle(string.Empty, true, _ => { })),
            }.Frame(width: 360f)));

    public static UiView Forms() => Page(
        new Specimen(
            "Form",
            "Sections of rows with a line between each, as in a settings screen. A header goes above a section and a note under it.",
            new Live<(bool Fullscreen, float Music, bool Advanced)>((true, 0.6f, false), (state, set) => new Form
            {
                new Section("Display")
                {
                    new Toggle("Fullscreen", state.Fullscreen, on => set(state with { Fullscreen = on })).Id("fullscreen"),
                    new LabeledContent("Resolution", "2560 × 1440"),
                },
                new Section("Audio", "Music fades out in menus.")
                {
                    new LabeledContent("Music", new Slider(state.Music, value => set(state with { Music = value })).Frame(width: 180f)),
                    new DisclosureGroup("Advanced", state.Advanced, open => set(state with { Advanced = open }))
                    {
                        new Toggle("Mono", false, _ => { }),
                        new Toggle("Night mode", true, _ => { }),
                    }.Id("advanced"),
                },
            }.Frame(width: 460f))),
        new Specimen(
            "Disclosure group",
            "A heading that shows or hides what's under it. Left alone it keeps whether it's open itself.",
            new VStack(HorizontalAlignment.Leading, 4f)
            {
                new DisclosureGroup("Keyboard") { new Text("WASD to move, Space to jump.").Foreground(Tone.Muted) }.Id("keyboard"),
                new DisclosureGroup("Gamepad") { new Text("Left stick to move, A to jump.").Foreground(Tone.Muted) },
            }.Frame(width: 360f)));

    public static UiView Tabs() => Page(
        new Specimen(
            "Tab view",
            "Pages a bar of tabs switches between. Left and right switch tabs on the focused bar, and the gamepad's bumpers from anywhere.",
            new TabView
            {
                new Tab("Video", Settings(new LabeledContent("Resolution", "2560 × 1440"), new LabeledContent("Frame limit", "144"))),
                new Tab("Audio", Settings(new LabeledContent("Output", "Speakers"), new LabeledContent("Music", "60%"))),
                new Tab("Controls", Settings(new LabeledContent("Invert look", "Off"), new LabeledContent("Sensitivity", "5"))),
            }.Frame(width: 460f, height: 200f)));

    public static UiView Navigation() => Page(
        new Specimen(
            "Navigation stack",
            "Pages that go deeper. The bar shows where you are and a way back, and Escape or the gamepad's East button goes back too.",
            new Live<NavigationPath<SettingsPage>>(new(), (path, _) => new NavigationStack<SettingsPage>(path, "Settings", Menu(path, SettingsPage.Graphics, SettingsPage.Audio), page => Destination(path, page))
                .Frame(width: 460f, height: 260f))));

    private static UiView Settings(params UiView[] rows)
    {
        var section = new Section();
        section.Add(rows);
        return section.Padding(0f, 16f);
    }

    private static UiView Menu(NavigationPath<SettingsPage> path, params SettingsPage[] pages)
    {
        var list = new VStack(HorizontalAlignment.Leading, 4f);
        foreach (var page in pages)
        {
            list.Add(new MenuRow(page.ToString(), () => path.Push(page)).Id(page.ToString().ToLowerInvariant()));
        }

        return list.Padding(8f);
    }

    private static UiView Destination(NavigationPath<SettingsPage> path, SettingsPage page) => page switch
    {
        SettingsPage.Graphics => new VStack(HorizontalAlignment.Leading, 12f)
        {
            new LabeledContent("Quality", "High"),
            Menu(path, SettingsPage.Advanced),
        }.Padding(16f),
        SettingsPage.Advanced => new VStack(HorizontalAlignment.Leading, 12f)
        {
            new LabeledContent("Shadow cascades", "4"),
            new LabeledContent("Probe updates", "Every frame"),
        }.Padding(16f),
        _ => new LabeledContent("Output", "Speakers").Padding(16f),
    };
}

/// <summary>A row that opens a page: its name, and a chevron at the trailing edge.</summary>
internal sealed record MenuRow(string Label, Action Open) : UiView
{
    public override UiView Body
    {
        get
        {
            var colors = Environment.Theme.Colors;
            var row = new Pressable(look => new HStack(Spacing: 8f)
            {
                new Text(Label),
                new Spacer(),
                new Icon(IconName.ChevronRight).Foreground(Tone.Muted),
            }
                .Padding(10f, 8f)
                .Frame(maxWidth: UiExtensions.Infinity)
                .Background(look.Hovered ? colors.Hover : default)
                .CornerRadius(6f), Open);
            return row.Semantics(UiRole.Button, Label);
        }
    }
}