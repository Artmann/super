using Super;
using Super.Ui;
using static StorybookExample.Layout;

namespace StorybookExample;

/// <summary>The story stories: dialogue, title cards and achievements, and subtitles.</summary>
internal static class Telling
{
    public static UiView Dialogue() => Page(
        new Specimen(
            "A conversation",
            "The line types itself out. Enter, Space, South or a click shows the rest, and the next press moves on. The second line asks, and its answers show once it's out. Hold the button over the box to skip it all.",
            new Conversation()));

    public static UiView TitleCards() => new Live<int>(0, (shown, show) => Page(
        new Specimen(
            "Title cards",
            "A big title in the middle of the screen that rises, holds and fades away by itself after Seconds. Leave Seconds out and it stays.",
            new VStack(HorizontalAlignment.Leading, 12f)
            {
                new Button("Show it again", () => show(shown + 1)) { Style = ButtonStyle.Outline }.Id("title-again"),
                Stage(new TitleCard("The Old Mill") { Subtitle = "Chapter 2", Seconds = 4f }.Id($"title-{shown}"), Alignment.Center),
            }),
        new Specimen(
            "Achievements",
            "A card that drops in, says what was earned and lifts away after five seconds. Give it a Picture for the achievement's own art.",
            new VStack(HorizontalAlignment.Leading, 12f)
            {
                new Button("Earn it again", () => show(shown + 1)) { Style = ButtonStyle.Outline }.Id("achievement-again"),
                Stage(new Achievement("Miller") { Description = "Fix the wheel at the old mill" }.Id($"achievement-{shown}").Padding(16f), Alignment.Top),
            })));

    public static UiView Subtitles() => Page(
        new Specimen(
            "Subtitles",
            "A SubtitleTrack finds the line said at a time, and Subtitles shows it at the bottom on a dark backing, with who says it. This scene plays over and over.",
            Stage(new Scene(), Alignment.Bottom)));

    /// <summary>A strip of screen, coloured like a dusky game, to show a full-screen view in.</summary>
    private static UiView Stage(UiView view, Alignment alignment) => view
        .Frame(width: 760f, height: 240f, alignment: alignment)
        .Background(Paint.Linear(Color.Hex("#312E81"), Color.Hex("#0B1020")))
        .CornerRadius(12f)
        .Id("stage");
}

/// <summary>Four lines, one of which asks a question, that start again from the top.</summary>
internal sealed record Conversation : UiView
{
    public override UiView Body
    {
        get
        {
            var step = UseState(0);
            var said = UseState("");
            var box = step.Value switch
            {
                0 => Mira("The bridge is out, so we'll have to go round by the old mill before the sun sets.") with { OnNext = () => step.Value = 1 },
                1 => Mira("Which way do we go?") with
                {
                    Choices =
                    [
                        new DialogueChoice("Take the mill road", () => step.Value = 2),
                        new DialogueChoice("Wait for morning", () => step.Value = 3),
                    ],
                },
                2 => Mira("The mill road it is. Stay close, and mind the water.") with { OnNext = () => step.Value = 0 },
                _ => Mira("Morning, then. I'll take the first watch.") with { OnNext = () => step.Value = 0 },
            };
            return new VStack(HorizontalAlignment.Leading, 12f)
            {
                (box with { OnSkip = () => { said.Value = "Skipped."; step.Value = 0; } }).Frame(width: 680f),
                said.Value.Length > 0 ? new Text(said.Value).Foreground(Tone.Muted) : null,
            };
        }
    }

    private static DialogueBox Mira(string line) => new("Mira", line)
    {
        Portrait = new Icon(IconName.Star).Foreground(Color.White).Frame(maxWidth: UiExtensions.Infinity, maxHeight: UiExtensions.Infinity).Background(Color.Hex("#7C3AED")),
    };
}

/// <summary>Subtitles for a short scene, on a clock that starts it again once it's over.</summary>
internal sealed record Scene : UiView
{
    private static readonly SubtitleTrack Track = new(
    [
        new SubtitleLine("Did you hear that?", 0.5f, 2.5f) { Speaker = "Mira" },
        new SubtitleLine("Only the wheel. It turns when the river's high.", 2.8f, 5.6f) { Speaker = "Oswin", SpeakerColor = Color.Hex("#F59E0B") },
        new SubtitleLine("[water rushing]", 5.9f, 7.4f),
        new SubtitleLine("Then why has it stopped?", 7.6f, 9.6f) { Speaker = "Mira" },
    ]);

    public override UiView Body
    {
        get
        {
            float seconds = (float)(UseTime() % (Track.Length + 1.5));
            return new Subtitles(Track.At(seconds)) { Size = 20f };
        }
    }
}
