using Super;
using Super.Ui;
using static StorybookExample.Layout;

namespace StorybookExample;

/// <summary>
/// The stories for the controls: buttons, toggles, sliders, steppers, pickers, progress and gauges. The
/// controls only show what they're given, so each story keeps its values in a <see cref="Live{T}"/>.
/// </summary>
internal static class Controls
{
    private static readonly PickerOption<string>[] Difficulties =
    [
        new("Story", "story"),
        new("Normal", "normal"),
        new("Hard", "hard"),
        new("Nightmare", "nightmare"),
    ];

    /// <summary>Graphics presets, which the settings showcase picks from too.</summary>
    public static readonly PickerOption<string>[] Presets =
    [
        new("Low", "low"),
        new("Medium", "medium"),
        new("High", "high"),
        new("Ultra", "ultra"),
    ];

    public static UiView Buttons() => Page(
        ButtonStyles(),
        ButtonIcons(),
        new Specimen(
            "Control sizes",
            "Small, regular and large, set for everything inside with .ControlSize(...).",
            Row(
                16f,
                new Button("Small", Clicked).ControlSize(ControlSize.Small),
                new Button("Regular", Clicked),
                new Button("Large", Clicked).ControlSize(ControlSize.Large))),
        new Specimen(
            "Turned off",
            "A button turned off fades and lets nothing happen, but still blocks the mouse.",
            Row(
                16f,
                new Button("Save"),
                new Button("Save", Disabled: true),
                new Button("Discard", Disabled: true) { Style = ButtonStyle.Outline })),
        new Specimen(
            "A group turned off",
            ".Disabled() turns off every control inside it, and further in can't turn them back on.",
            Row(
                12f,
                new Button("Back") { Style = ButtonStyle.Outline },
                new Button("Next") { Style = ButtonStyle.Secondary },
                new Button("Finish").Disabled(false)).Disabled()));

    private static Specimen ButtonStyles() =>
        new(
            "Styles",
            "Default for the main action, then secondary, outline, ghost, destructive and link.",
            Row(
                12f,
                new Button("Continue", Clicked),
                new Button("Options", Clicked) { Style = ButtonStyle.Secondary },
                new Button("Credits", Clicked) { Style = ButtonStyle.Outline },
                new Button("Skip", Clicked) { Style = ButtonStyle.Ghost },
                new Button("Delete save", Clicked) { Style = ButtonStyle.Destructive },
                new Button("Patch notes", Clicked) { Style = ButtonStyle.Link }));

    private static Specimen ButtonIcons() =>
        new(
            "With icons",
            "An icon before the label, or only an icon in a square button.",
            Row(
                12f,
                new Button("New game", Clicked) { Icon = IconName.Plus },
                new Button("Search", Clicked) { Style = ButtonStyle.Outline, Icon = IconName.Search },
                new Button("Next", Clicked) { Style = ButtonStyle.Secondary, Icon = IconName.ArrowRight },
                new Button("", Clicked) { Style = ButtonStyle.Outline, Icon = IconName.Menu },
                new Button("", Clicked) { Style = ButtonStyle.Ghost, Icon = IconName.Close }));

    public static UiView Toggles() => Page(
        new Specimen(
            "Switches",
            "A switch fills its row, with the label at the start. Click the label or the switch.",
            new Live<(bool Music, bool Subtitles, bool Shake)>(
                (true, false, true),
                (on, set) => new VStack(HorizontalAlignment.Leading, 14f)
                {
                    new Toggle("Music", on.Music, music => set(on with { Music = music })).Id("music"),
                    new Toggle("Subtitles", on.Subtitles, subtitles => set(on with { Subtitles = subtitles })).Id("subtitles"),
                    new Toggle("Camera shake", on.Shake, shake => set(on with { Shake = shake })).Id("shake"),
                }.Frame(width: 320f))),
        new Specimen(
            "Checkboxes",
            "A box before its label, for lists of things to turn on.",
            new Live<(bool Terms, bool News)>(
                (true, false),
                (on, set) => new VStack(HorizontalAlignment.Leading, 12f)
                {
                    new Toggle("Accept the terms", on.Terms, terms => set(on with { Terms = terms })) { Style = ToggleStyle.Checkbox }.Id("terms"),
                    new Toggle("Send me news", on.News, news => set(on with { News = news })) { Style = ToggleStyle.Checkbox }.Id("news"),
                })),
        new Specimen(
            "Turned off",
            "On and off, both faded, and clicks pass them by.",
            Row(
                32f,
                new Toggle("", true, Disabled: true),
                new Toggle("", false, Disabled: true),
                new Toggle("Locked", true, Disabled: true) { Style = ToggleStyle.Checkbox })));

    public static UiView Sliders() => Page(
        new Specimen(
            "Smooth",
            "Drag the thumb, or press anywhere on the track to jump there.",
            new Live<float>(
                0.7f,
                (volume, set) => new HStack(Spacing: 16f)
                {
                    new Label("Volume", IconName.Info).Foreground(Tone.Muted),
                    new Slider(volume, set) { Label = "Volume" }.Id("volume"),
                    new Text(MathF.Round(volume * 100f).ToString(System.Globalization.CultureInfo.InvariantCulture)).MonospacedDigit().Frame(width: 32f, alignment: Alignment.Trailing),
                }.Frame(width: 420f))),
        new Specimen(
            "In steps",
            "With a step, the value moves in whole numbers, here from 60 to 120.",
            new Live<float>(
                90f,
                (fov, set) => new VStack(HorizontalAlignment.Leading, 8f)
                {
                    new HStack { new Text("Field of view"), new Spacer(), new Text(UiSemantics.Number(fov)).Foreground(Tone.Muted).MonospacedDigit() },
                    new Slider(fov, set, 60f, 120f) { Step = 5f, Label = "Field of view" }.Id("fov"),
                }.Frame(width: 420f))),
        new Specimen(
            "Sizes and turned off",
            "A slider follows the control size, and fades when turned off.",
            new VStack(HorizontalAlignment.Leading, 4f)
            {
                new Slider(0.3f) { Label = "Small" }.ControlSize(ControlSize.Small),
                new Slider(0.5f) { Label = "Regular" },
                new Slider(0.7f) { Label = "Large" }.ControlSize(ControlSize.Large),
                new Slider(0.4f) { Label = "Off", Disabled = true },
            }.Frame(width: 420f)));

    public static UiView Steppers() => Page(
        new Specimen(
            "Counts",
            "Minus and plus, each turning off at its end of the range.",
            new Live<(int Arrows, int Players)>(
                (12, 1),
                (count, set) => new VStack(HorizontalAlignment.Leading, 12f)
                {
                    new Stepper("Arrows", count.Arrows, arrows => set(count with { Arrows = arrows }), 0, 99) { Step = 6 }.Id("arrows"),
                    new Stepper("Players", count.Players, players => set(count with { Players = players }), 1, 4).Id("players"),
                }.Frame(width: 320f))),
        new Specimen(
            "Without a label, and turned off",
            "Only the buttons and the number.",
            Row(32f, new Stepper("", 3, Min: 0, Max: 5) { Disabled = true }, new Stepper("", 0, Min: 0, Max: 5))));

    public static UiView Pickers() => Page(
        new Specimen(
            "Segmented",
            "One of a few, side by side, the picked one raised.",
            new Live<string>(
                "normal",
                (difficulty, set) => new Picker<string>("Difficulty", difficulty, Difficulties, set).Id("difficulty").Frame(width: 420f))),
        new Specimen(
            "Radio",
            "One under the other, for choices with longer names or more of them.",
            new Live<string>(
                "high",
                (preset, set) => new Picker<string>("Graphics preset", preset, Presets, set) { Style = PickerStyle.Radio }.Id("preset"))),
        new Specimen(
            "Small and turned off",
            "A segmented picker follows the control size.",
            new VStack(HorizontalAlignment.Leading, 12f)
            {
                new Picker<string>("Small", "low", Presets).ControlSize(ControlSize.Small),
                new Picker<string>("Off", "medium", Presets) { Disabled = true },
            }.Frame(width: 320f)));

    public static UiView Progress() => Page(
        new Specimen(
            "Bars",
            "How far along something is. With a label, the percentage shows beside it.",
            new VStack(HorizontalAlignment.Leading, 18f)
            {
                new ProgressView("Downloading update", 0.35f),
                new ProgressView("Shaders", 412f, 412f),
                new ProgressView("", 0.7f),
            }.Frame(width: 420f)),
        new Specimen(
            "Rings",
            "The percentage inside, in each control size.",
            Row(
                24f,
                new ProgressView("Small", 0.25f) { Style = MeterStyle.Circular }.ControlSize(ControlSize.Small),
                new ProgressView("Regular", 0.6f) { Style = MeterStyle.Circular },
                new ProgressView("Large", 0.9f) { Style = MeterStyle.Circular }.ControlSize(ControlSize.Large))));

    public static UiView Gauges() => Page(
        new Specimen(
            "Rings",
            "A reading on a scale, open at the bottom, with its own colour.",
            Row(
                24f,
                new Gauge("Health", 72f, 0f, 100f) { Tint = Color.Hex("#22C55E") },
                new Gauge("Speed", 140f, 0f, 240f) { ValueLabel = "140" },
                new Gauge("Heat", 0.92f) { Tint = Color.Hex("#EF4444"), ValueLabel = "92%" })),
        new Specimen(
            "Bars",
            "The label and reading above, and the ends of the scale below.",
            new VStack(HorizontalAlignment.Leading, 18f)
            {
                new Gauge("Stamina", 35f, 0f, 50f) { Style = MeterStyle.Linear, Tint = Color.Hex("#F59E0B") },
                new Gauge("Temperature", -4f, -20f, 40f) { Style = MeterStyle.Linear, ValueLabel = "-4 °C" },
            }.Frame(width: 420f)));

    private static void Clicked()
    {
    }
}

/// <summary>Keeps a story's values between frames, as a game would keep them in its own state.</summary>
internal sealed record Live<T>(T Initial, Func<T, Action<T>, UiView> Show) : UiView
{
    public override UiView Body
    {
        get
        {
            var state = UseState(Initial);
            return Show(state.Value, value => state.Value = value);
        }
    }
}
