using Super.Ui;
using static StorybookExample.Layout;

namespace StorybookExample;

/// <summary>The stories for the small elements: badges, labels, dividers and key hints.</summary>
internal static class Elements
{
    public static UiView Badges() => Page(
        new Specimen(
            "Styles",
            "Default for what matters most, secondary for counts and tags, destructive for trouble, outline for the quietest.",
            Row(
                12f,
                new Badge("Default"),
                new Badge("Secondary", BadgeStyle.Secondary),
                new Badge("Destructive", BadgeStyle.Destructive),
                new Badge("Outline", BadgeStyle.Outline))),
        new Specimen(
            "Beside text",
            "A badge sits on the line of the text next to it.",
            new VStack(HorizontalAlignment.Leading, 14f)
            {
                Row(10f, new Text("Ember Blade").TextStyle(TextStyle.Headline), new Badge("Legendary")),
                Row(10f, new Text("Quests"), new Badge("3", BadgeStyle.Secondary)),
                Row(10f, new Text("Server: Frankfurt"), new Badge("Offline", BadgeStyle.Destructive)),
                Row(10f, new Text("Photo mode"), new Badge("Beta", BadgeStyle.Outline)),
            }));

    public static UiView Labels() => Page(
        new Specimen(
            "With icons",
            "The icon takes the text's size and colour.",
            new VStack(HorizontalAlignment.Leading, 12f)
            {
                new Label("Search", IconName.Search),
                new Label("Add to party", IconName.Plus),
                new Label("Settings", IconName.Menu),
                new Label("About this build", IconName.Info).Foreground(Tone.Muted),
                new Label("Saved", IconName.Check).Foreground(Tone.Success),
            }),
        new Specimen(
            "Sizes",
            "Change the font size and the icon grows with it.",
            new VStack(HorizontalAlignment.Leading, 12f)
            {
                new Label("Continue", IconName.ArrowRight).TextStyle(TextStyle.Footnote),
                new Label("Continue", IconName.ArrowRight),
                new Label("Continue", IconName.ArrowRight).TextStyle(TextStyle.Title3),
                new Label("Continue", IconName.ArrowRight).TextStyle(TextStyle.Title),
            }));

    public static UiView Dividers() => Page(
        new Specimen(
            "Across a list",
            "In a vertical stack, a divider runs from side to side.",
            new VStack(HorizontalAlignment.Leading, 10f)
            {
                new Text("Audio"),
                new Divider(),
                new Text("Video"),
                new Divider(),
                new Text("Controls"),
            }.Frame(width: 280f)),
        new Specimen(
            "In a toolbar",
            "In a horizontal stack, it stands upright.",
            Row(
                14f,
                new Label("Back", IconName.ChevronLeft),
                new Divider(),
                new Text("Inventory").TextStyle(TextStyle.Headline),
                new Divider(),
                new Label("Sort", IconName.Menu)).Frame(height: 24f)));

    public static UiView KeyHints() => Page(
        new Specimen(
            "Keys",
            "A keycap is at least square, and grows for a long name.",
            Row(10f, new KeyHint("E"), new KeyHint("Q"), new KeyHint("Esc"), new KeyHint("Tab"), new KeyHint("Shift"), new KeyHint("Space"))),
        new Specimen(
            "Prompts",
            "Inline with text, for what to press.",
            new VStack(HorizontalAlignment.Leading, 14f)
            {
                Row(8f, new Text("Press"), new KeyHint("E"), new Text("to open the chest")),
                Row(6f, new Text("Save"), new Spacer().Frame(width: 4f), new KeyHint("Ctrl"), new Text("+").Foreground(Tone.Muted), new KeyHint("S")),
                Row(8f, new Text("Hold"), new KeyHint("Shift"), new Text("to run")),
            }));
}
