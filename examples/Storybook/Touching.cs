using Super;
using Super.Ui;
using static StorybookExample.Layout;

namespace StorybookExample;

/// <summary>The story for fingers: taps, drags that scroll and fling, long presses, and lists that go sideways.</summary>
internal static class Touching
{
    private static readonly string[] Swatches =
    [
        "#E11D48", "#EA580C", "#CA8A04", "#65A30D", "#059669", "#0891B2",
        "#2563EB", "#7C3AED", "#C026D3", "#78716C", "#475569", "#0F766E",
    ];

    public static UiView Touch() => Page(
        new Specimen(
            "Tap, hold, drag and fling",
            "A tap picks a quest, and holding one still for half a second pins it. Drag the list and it follows the finger, and a quick swipe flings it on until it slows. A drag never picks. The mouse picks and scrolls it too.",
            new QuestList()),
        new Specimen(
            "Sideways inside up and down",
            "A drag goes to the scroll view that runs the way the finger mostly moved, so this strip goes sideways and the page still scrolls.",
            SwatchStrip()));

    private static UiView SwatchStrip()
    {
        var strip = new HStack(Spacing: 12f);
        for (int i = 0; i < Swatches.Length; i++)
        {
            strip.Add(Captioned(
                new Rectangle().Fill(Color.Hex(Swatches[i])).Frame(96f, 64f).CornerRadius(8f),
                Swatches[i]));
        }

        // Room under the captions for the scroll bar.
        return new ScrollView(Axis.Horizontal) { strip.Padding(0f, 0f, 14f, 0f) }.Frame(width: 420f).Id("swatches");
    }
}

/// <summary>Twenty quests to scroll through, one picked with a tap and one pinned with a long press.</summary>
internal sealed record QuestList : UiView
{
    private static readonly string[] Quests =
    [
        "The lost lantern", "Wolves at the mill", "A letter for the abbot", "Salt for the winter",
        "The drowned bell", "Smoke on the ridge", "Three keys", "The ferryman's debt",
        "Moss and iron", "A song for the dead", "The quiet road", "Herbs for the healer",
        "The broken oath", "Ash in the well", "A crown of reeds", "The last orchard",
        "Ravens over the keep", "The tinker's cart", "Frost in summer", "The empty throne",
    ];

    public override UiView Body
    {
        get
        {
            var theme = Environment.Theme;
            var picked = UseState("none yet");
            var pinned = UseState("none");
            var list = new VStack(HorizontalAlignment.Leading, 2f);
            for (int i = 0; i < Quests.Length; i++)
            {
                string quest = Quests[i];
                var row = new HStack(VerticalAlignment.Center, 12f) { new Text(quest), new Spacer() };
                if (pinned.Value == quest)
                {
                    row.Add(new Badge("Pinned"));
                }

                list.Add(row
                    .Padding(12f, 10f)
                    .Frame(maxWidth: UiExtensions.Infinity)
                    .Background(picked.Value == quest ? theme.Colors.Hover : default)
                    .CornerRadius(theme.Radius)
                    .OnClick(() => picked.Value = quest)
                    .OnLongPress(() => pinned.Value = quest)
                    .Semantics(UiRole.Button, quest)
                    .Id($"quest-{i + 1}"));
            }

            return new VStack(HorizontalAlignment.Leading, 12f)
            {
                new ScrollView { list }.Frame(width: 420f, height: 240f).Id("quests"),
                new Text($"Picked: {picked.Value}. Pinned: {pinned.Value}.").Foreground(Tone.Muted),
            };
        }
    }
}
